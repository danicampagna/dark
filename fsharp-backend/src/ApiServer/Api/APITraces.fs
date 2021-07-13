module ApiServer.Traces

// API endpoints for Traces

open Microsoft.AspNetCore.Http
open Giraffe
open Giraffe.EndpointRouting

open System.Threading.Tasks
open FSharp.Control.Tasks
open FSharpPlus
open Prelude
open Tablecloth

module PT = LibExecution.ProgramTypes
module ORT = LibExecution.OCamlTypes.RuntimeT
module AT = LibExecution.AnalysisTypes

module Traces = LibBackend.Traces
module Canvas = LibBackend.Canvas
module Convert = LibExecution.OCamlTypes.Convert

module TraceData =
  type Params = { tlid : tlid; trace_id : AT.TraceID }

  // CLEANUP: this uses ORT.dval instead of RT.Dval
  type InputVars = List<string * ORT.dval>
  type FunctionArgHash = string
  type HashVersion = int
  type FnName = string
  type FunctionResult = FnName * id * FunctionArgHash * HashVersion * ORT.dval

  type TraceData =
    { input : InputVars
      timestamp : System.DateTime
      function_results : List<FunctionResult> }

  type Trace = AT.TraceID * TraceData
  type TraceResult = { trace : Trace }

  type T = Option<TraceResult>

  let getTraceData (ctx : HttpContext) : Task<T> =
    task {
      let t = Middleware.startTimer ctx
      let canvasInfo = Middleware.loadCanvasInfo ctx
      let! p = ctx.BindModelAsync<Params>()
      t "read-api"

      let! (c : Canvas.T) =
        Canvas.loadTLIDsFromCache canvasInfo [ p.tlid ]
        |> Task.map Result.unwrapUnsafe

      t "load-canvas"

      // TODO: we dont need the handlers or functions at all here, just for the sample
      // values which we can do on the client instead
      let handler = c.handlers |> Map.get p.tlid

      let! trace =
        match handler with
        | Some h -> Traces.handlerTrace c.meta.id p.trace_id h |> Task.map Some
        | None ->
            match c.userFunctions |> Map.get p.tlid with
            | Some u -> Traces.userfnTrace c.meta.id p.trace_id u |> Task.map Some
            | None -> task { return None }

      t "load-trace"

      // CLEANUP, this is shimming an RT.Dval into an ORT.dval. Nightmare.
      let (trace : Option<Trace>) =
        match trace with
        | Some (id, (traceData : AT.TraceData)) ->
            Some(
              id,
              { input =
                  List.map
                    (fun (s, dv) -> (s, Convert.rt2ocamlDval dv))
                    traceData.input
                timestamp = traceData.timestamp
                function_results =
                  List.map
                    (fun (r1, r2, r3, r4, dv) ->
                      (r1, r2, r3, r4, Convert.rt2ocamlDval dv))
                    traceData.function_results }
            )
        | None -> None

      t "write-api"
      return Option.map (fun t -> { trace = t }) trace
    }

module AllTraces =

  type T = { traces : List<tlid * AT.TraceID> }

  let fetchAll (ctx : HttpContext) : Task<T> =
    task {
      let t = Middleware.startTimer ctx
      let canvasInfo = Middleware.loadCanvasInfo ctx
      t "read-api"

      // CLEANUP we only need the HTTP handler paths here, so we can remove the loadAll
      // CLEANUP don't load traces for deleted handlers
      let! (c : Canvas.T) = Canvas.loadAll canvasInfo |> Task.map Result.unwrapUnsafe
      t "load-canvas"

      let! hTraces =
        c.handlers
        |> Map.values
        |> List.map
             (fun h ->
               Traces.traceIDsForHandler c h
               |> Task.map (List.map (fun traceid -> (h.tlid, traceid))))
        |> Task.flatten
        |> Task.map List.concat

      t "fetch-handler-traces"

      let! ufTraces =
        c.userFunctions
        |> Map.values
        |> List.map
             (fun uf ->
               Traces.traceIDsForUserFn c.meta.id uf.tlid
               |> Task.map (List.map (fun traceID -> (uf.tlid, traceID))))
        |> Task.flatten
        |> Task.map List.concat

      t "fetch-userfn-traces"
      // FSTODO pageable

      return { traces = hTraces @ ufTraces }
    }
