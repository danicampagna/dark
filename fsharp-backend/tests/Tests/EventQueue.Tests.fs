module Tests.EventQueue

open System.Threading.Tasks
open FSharp.Control.Tasks

open Expecto

open Prelude
open Prelude.Tablecloth
open Tablecloth
open LibBackend.Db

open TestUtils

module PT = LibExecution.ProgramTypes
module RT = LibExecution.RuntimeTypes
module EQ = LibBackend.EventQueue
module Canvas = LibBackend.Canvas
module QW = LibBackend.QueueWorker
module Serialize = LibBackend.Serialize
module Span = LibService.Telemetry.Span

module TI = LibBackend.TraceInputs
module TFR = LibBackend.TraceFunctionResults


let p (code : string) = FSharpToExpr.parsePTExpr code

// This doesn't actually test input, since it's a cron handler and not an actual event handler

let testEventQueueRoundtrip =
  testTask "event queue roundtrip" {
    let name = "test-event_queue"
    do! clearCanvasData (CanvasName.create name)
    let! (meta : Canvas.Meta) = testCanvasInfo name
    let executionID = gid ()

    let h = testCron "test" PT.Handler.EveryDay (p "let data = Date.now_v0 in 123")
    let oplists = [ hop h ]

    do!
      Canvas.saveTLIDs meta [ (h.tlid, oplists, PT.TLHandler h, Canvas.NotDeleted) ]

    do! EQ.enqueue meta.id meta.owner "CRON" "test" "Daily" RT.DNull // I don't believe crons take inputs?

    do! EQ.testingScheduleAll ()
    let! result = QW.run executionID

    match result with
    | Ok (Some resultDval) ->
        // should have at least one trace
        let! eventIDs = TI.loadEventIDs meta.id ("CRON", "test", "Daily")
        let traceID = eventIDs |> List.head |> Option.unwrapUnsafe |> Tuple2.first

        let! functionResults = TFR.load meta.id traceID h.tlid

        Expect.equal (List.length functionResults) 1 "should have stored fn result"
        Expect.equal (RT.DInt 123I) resultDval "Round tripped value"
    | Ok None -> failwith "Failed: expected Some, got None"
    | Error e -> failwith $"Failed: got error: {e}"
  }


let testEventQueueIsFifo =
  testTask "event queue is fifo" {
    let name = "fifo"
    do! clearCanvasData (CanvasName.create name)
    let! meta = testCanvasInfo name
    let apple = testWorker "apple" (p "event")
    let banana = testWorker "banana" (p "event")

    do!
      ([ apple; banana ]
       |> List.map (fun h -> (h.tlid, [ hop h ], PT.TLHandler h, Canvas.NotDeleted))
       |> Canvas.saveTLIDs meta)

    let enqueue (name : string) (i : bigint) =
      EQ.enqueue meta.id meta.owner "WORKER" name "_" (RT.DInt i)

    do! enqueue "apple" 1I
    do! enqueue "apple" 2I
    do! enqueue "banana" 3I
    do! enqueue "apple" 4I
    do! EQ.testingScheduleAll ()

    let checkDequeue span (i : bigint) exname : Task<unit> =
      task {
        let! evt = EQ.dequeue span
        let evt = Option.unwrapUnsafe evt

        Expect.equal exname evt.name $"dequeue {i} is handler {exname}"
        Expect.equal evt.value (RT.DInt i) $"dequeue {i} has value {i}"
        do! EQ.finish span evt
        return ()
      }

    use span = Span.root "test"

    do!
      Sql.withTransaction
        (fun () ->
          task {
            do! checkDequeue span 1I "apple"
            do! checkDequeue span 2I "apple"
            do! checkDequeue span 3I "banana"
            do! checkDequeue span 4I "apple"
            return Ok(Some RT.DNull)
          })
  }

let testGetWorkerSchedulesForCanvas =
  testTask "worker schedules for canvas" {
    let name = "worker-schedules"
    do! clearCanvasData (CanvasName.create name)
    let! meta = testCanvasInfo name

    let apple = testWorker "apple" (p "1")
    let banana = testWorker "banana" (p "1")
    let cherry = testWorker "cherry" (p "1")

    do!
      ([ apple; banana; cherry ]
       |> List.map (fun h -> (h.tlid, [ hop h ], PT.TLHandler h, Canvas.NotDeleted))
       |> Canvas.saveTLIDs meta)

    do! EQ.pauseWorker meta.id "apple"
    do! EQ.pauseWorker meta.id "banana"
    do! EQ.blockWorker meta.id "banana"
    let! result = EQ.getWorkerSchedules meta.id

    let check (name : string) (value : EQ.WorkerStates.State) =
      let actual = Map.get name result |> Option.unwrapUnsafe |> toString
      let expected = toString value
      Expect.equal actual expected ($"{name} is {expected}")

    check "apple" EQ.WorkerStates.Paused
    check "banana" EQ.WorkerStates.Blocked
    check "cherry" EQ.WorkerStates.Running
  }

let tests =
  testList
    "eventQueue"
    [ testEventQueueRoundtrip
      testEventQueueIsFifo
      testGetWorkerSchedulesForCanvas ]
