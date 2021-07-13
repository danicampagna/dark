namespace Wasm

open System.Threading.Tasks
open FSharp.Control.Tasks

open Prelude
open Tablecloth

module RT = LibExecution.RuntimeTypes
module Exe = LibExecution.Execution
module PT = LibExecution.ProgramTypes
module AT = LibExecution.AnalysisTypes
module OT = LibExecution.OCamlTypes
module ORT = LibExecution.OCamlTypes.RuntimeT
module DvalRepr = LibExecution.DvalRepr

module ClientInterop =
  // CLEANUP There is a subtly different definition of DBs, which is that the
  // type should be a DType, but the client gives us a string instead.

  type client_col = string OT.or_blank * string OT.or_blank

  type client_db_migration =
    { starting_version : int64
      version : int64
      state : ORT.DbT.db_migration_state
      rollforward : ORT.fluidExpr
      rollback : ORT.fluidExpr
      cols : client_col list }

  type client_db =
    { tlid : tlid
      name : string OT.or_blank
      cols : client_col list
      version : int64
      old_migrations : client_db_migration list
      active_migration : client_db_migration option }

  let convert_col ((name, tipe) : client_col) : ORT.DbT.col =
    match tipe with
    | OT.Blank id -> (name, OT.Blank id)
    | OT.Partial (id, str) -> (name, OT.Partial(id, str))
    | OT.Filled (id, tipe) ->
        (name, (OT.Filled(id, PT.DType.parse tipe |> OT.Convert.pt2ocamlTipe)))

  let convert_migration
    (m : client_db_migration)
    : ORT.fluidExpr ORT.DbT.db_migration =
    { starting_version = m.starting_version
      version = m.version
      state = m.state
      rollforward = m.rollforward
      rollback = m.rollback
      cols = List.map convert_col m.cols }


  let convert_db (db : client_db) : ORT.fluidExpr ORT.DbT.db =
    { tlid = db.tlid
      name = db.name
      cols = List.map convert_col db.cols
      version = db.version
      old_migrations = List.map convert_migration db.old_migrations
      active_migration = Option.map convert_migration db.active_migration }

  type handler_analysis_param =
    { handler : ORT.fluidExpr ORT.HandlerT.handler
      trace_id : AT.TraceID
      trace_data : AT.TraceData
      (* dont use a trace as this isn't optional *)
      dbs : client_db list
      user_fns : ORT.fluidExpr ORT.user_fn list
      user_tipes : ORT.user_tipe list
      secrets : OT.secret list }

  type function_analysis_param =
    { func : ORT.fluidExpr ORT.user_fn
      trace_id : AT.TraceID
      trace_data : AT.TraceData
      (* dont use a trace as this isn't optional *)
      dbs : client_db list
      user_fns : ORT.fluidExpr ORT.user_fn list
      user_tipes : ORT.user_tipe list
      secrets : OT.secret list }

  type performAnalysisParams =
    | AnalyzeHandler of handler_analysis_param // called performHandlerAnalysisParam in the client
    | AnalyzeFunction of function_analysis_param // called performHandlerFunctionParam in the client

  type ExecutionResult =
    | ExecutedResult of ORT.dval
    | NonExecutedResult of ORT.dval

  // Dictionarys are mutable
  type AnalysisResults = System.Collections.Generic.Dictionary<id, ExecutionResult>

  type AnalysisEnvelope = AT.TraceID * AnalysisResults

module Eval =

  let loadFromTrace
    (results : AT.FunctionResult list)
    ((_tlid, fnName, callerID) : RT.FunctionRecord)
    (args : List<RT.Dval>)
    : Option<RT.Dval * System.DateTime> =
    let hashes =
      DvalRepr.supportedHashVersions
      |> List.map (fun key -> (key, DvalRepr.hash key args))
      |> Map

    results
    |> List.filterMap
         (fun (rFnName, rCallerID, hash, hashVersion, dval) ->
           if string fnName = rFnName
              && callerID = rCallerID
              && hash = ((Map.tryFind hashVersion hashes) |> Option.unwrapUnsafe) then
             Some dval
           else
             None)
    |> List.head
    (* We don't use the time, so just hack it to get the interface right. *)
    |> Option.map (fun dv -> (dv, System.DateTime.Now))



  let runAnalysis
    (tlid : tlid)
    (traceID : AT.TraceID)
    (traceData : AT.TraceData)
    (userFns : List<ORT.fluidExpr ORT.user_fn>)
    (userTypes : List<ORT.user_tipe>)
    (dbs : List<ORT.fluidExpr ORT.DbT.db>)
    (expr : ORT.fluidExpr)
    : Task<ClientInterop.AnalysisEnvelope> =
    task {
      let program : RT.ProgramContext =
        { accountID = System.Guid.NewGuid()
          canvasID = System.Guid.NewGuid()
          userFns =
            userFns
            |> List.map OT.Convert.ocamlUserFunction2PT
            |> List.map PT.UserFunction.toRuntimeType
            |> List.map (fun fn -> fn.name, fn)
            |> Map
          userTypes =
            userTypes
            |> List.map OT.Convert.ocamlUserType2PT
            |> List.map PT.UserType.toRuntimeType
            |> List.map (fun t -> (t.name, t.version), t)
            |> Map
          dbs =
            dbs
            |> List.map (OT.Convert.ocamlDB2PT { x = 0; y = 0 })
            |> List.map PT.DB.toRuntimeType
            |> List.map (fun t -> t.name, t)
            |> Map
          secrets = [] }

      let stdlib =
        LibExecution.StdLib.StdLib.fns
        |> Map.fromListBy (fun fn -> RT.FQFnName.Stdlib fn.name)

      // FSTODO: get packages from caller
      let libraries : RT.Libraries = { stdlib = stdlib; packageFns = Map.empty }
      let dvalResults, traceDvalFn = Exe.traceDvals ()
      let functionResults = traceData.function_results

      let tracing =
        { LibExecution.Execution.noTracing RT.Preview with
            traceDval = traceDvalFn
            loadFnResult = loadFromTrace functionResults }

      let state = Exe.createState libraries tracing tlid program

      let ast = (expr |> OT.Convert.ocamlExpr2PT).toRuntimeType ()
      let inputVars = Map traceData.input
      let! (_result : RT.Dval) = Exe.executeExpr state inputVars ast


      let ocamlResults =
        dvalResults
        |> Dictionary.toList
        |> List.map
             (fun (k, v) ->
               k,
               match v with
               | AT.ExecutedResult dv ->
                   ClientInterop.ExecutedResult(OT.Convert.rt2ocamlDval dv)
               | AT.NonExecutedResult dv ->
                   ClientInterop.NonExecutedResult(OT.Convert.rt2ocamlDval dv))
        |> Dictionary.fromList

      return (traceID, ocamlResults)
    }

  let performAnalysis
    (args : ClientInterop.performAnalysisParams)
    : Task<ClientInterop.AnalysisEnvelope> =
    match args with
    | ClientInterop.AnalyzeHandler ah ->
        runAnalysis
          ah.handler.tlid
          ah.trace_id
          ah.trace_data
          ah.user_fns
          ah.user_tipes
          (List.map ClientInterop.convert_db ah.dbs)
          ah.handler.ast
    | ClientInterop.AnalyzeFunction af ->
        runAnalysis
          af.func.tlid
          af.trace_id
          af.trace_data
          af.user_fns
          af.user_tipes
          (List.map ClientInterop.convert_db af.dbs)
          af.func.ast



open System
open System.Reflection

#nowarn "988"

type GetGlobalObjectDelegate = delegate of string -> obj

// This is F# equivalant of the C# delegate:
// public delegate object InvokeDelegate(string m, params object[] ps);
type InvokeDelegate = delegate of m : string * [<ParamArray>] ps : obj [] -> obj

type EvalWorker =
  // Create a delegate with which to call self.postMessage
  static member getPostMessageDelegate() : InvokeDelegate =
    let assemblyName = "System.Private.Runtime.InteropServices.JavaScript"
    let typeName = "System.Runtime.InteropServices.JavaScript.Runtime"

    // Get the `self` object from the webworker.
    let sourceAssembly : Assembly = Assembly.Load(assemblyName)
    let typ = sourceAssembly.GetType(typeName)
    // I do not have any clue what this does
    let method = typ.GetMethod(nameof (EvalWorker.GetGlobalObject))
    let delegate_ = method.CreateDelegate<GetGlobalObjectDelegate>()
    let target = delegate_.Invoke("self")

    // Get a `postMessage` method from the `self` object
    let typ = target.GetType()
    let invokeMethod = typ.GetMethod("Invoke")

    System.Delegate.CreateDelegate(typeof<InvokeDelegate>, target, invokeMethod)
    :?> InvokeDelegate

  static member GetGlobalObject(globalObjectName : string) : unit = ()

  static member postMessageDelegate = EvalWorker.getPostMessageDelegate ()

  static member postMessage(message : string) : unit =
    let (_ : obj) = EvalWorker.postMessageDelegate.Invoke("postMessage", message)
    ()

  // receive messages from the BlazorWorker.js
  member this.OnMessage(message : string) =
    task {
      try
        let args =
          Json.OCamlCompatible.deserialize<ClientInterop.performAnalysisParams>
            message

        let! result = Eval.performAnalysis args
        return Ok result
      with e ->
        System.Console.WriteLine("Error running analysis in Blazor")
        System.Console.WriteLine(e)
        return Error(string e)
    }
    |> Task.map Json.OCamlCompatible.serialize
    |> Task.map EvalWorker.postMessage
    |> ignore
