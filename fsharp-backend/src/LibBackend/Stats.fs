module LibBackend.Stats

open System.Threading.Tasks
open FSharp.Control.Tasks

open Npgsql.FSharp.Tasks
open Npgsql
open Db

open Prelude
open Tablecloth

module RT = LibExecution.RuntimeTypes
module AT = LibExecution.AnalysisTypes
module PT = LibExecution.ProgramTypes

// -------------------------
// Non-execution analysis
// -------------------------

type DBStat = { count : int; example : Option<RT.Dval * string> }

type DBStats = Map<tlid, DBStat>

let dbStats (c : Canvas.T) (tlids : tlid list) : Task<DBStats> =
  let canvasID = c.meta.id
  let ownerID = c.meta.owner

  tlids
  |> List.filterMap
       (fun tlid -> Map.get tlid c.dbs |> Option.map (fun db -> (tlid, db)))
  |> List.map
       (fun (tlid, db) ->
         task {
           let db = PT.DB.toRuntimeType db
           // CLEANUP this is a lot of reqs
           let! count = UserDB.statsCount canvasID ownerID db
           let! example = UserDB.statsPluck canvasID ownerID db
           return (tlid, { count = count; example = example })
         })
  |> Task.flatten
  |> Task.map Map.ofList


let workerStats (canvasID : CanvasID) (tlid : tlid) : Task<int> =
  Sql.query
    "SELECT COUNT(1) AS num
     FROM events E
     INNER JOIN toplevel_oplists TL
        ON TL.canvas_id = E.canvas_id
       AND TL.module = E.space
       AND TL.name = E.name
     WHERE TL.tlid = @tlid
       AND TL.canvas_id = @canvasID
       AND E.status IN ('new', 'scheduled')"
  |> Sql.parameters [ "tlid", Sql.tlid tlid; "canvasID", Sql.uuid canvasID ]
  |> Sql.executeRowAsync (fun read -> read.int "num")
