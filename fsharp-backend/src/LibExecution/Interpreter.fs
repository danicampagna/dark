﻿module LibExecution.Interpreter

open System.Threading.Tasks
open FSharp.Control.Tasks
open FSharpPlus

open Prelude
open RuntimeTypes

let globalsFor (state : ExecutionState) : Symtable =
  let secrets =
    state.program.secrets
    |> List.map (fun (s : Secret.T) -> (s.secretName, DStr s.secretValue))
    |> Map.ofList

  let dbs = Map.map (fun _ (db : DB.T) -> DDB db.name) state.program.dbs
  Map.union secrets dbs


let withGlobals (state : ExecutionState) (symtable : Symtable) : Symtable =
  let globals = globalsFor state
  Map.union globals symtable



// fsharplint:disable FL0039
let rec eval' (state : ExecutionState) (st : Symtable) (e : Expr) : DvalTask =
  let sourceID id = SourceID(state.tlid, id)
  let incomplete id = DIncomplete(SourceID(state.tlid, id))

  let preview st expr : TaskOrValue<unit> =
    taskv {
      if state.tracing.realOrPreview = Preview then
        let state = { state with onExecutionPath = false }
        let! (_result : Dval) = eval state st expr
        return ()
    }


  taskv {
    match e with
    | EBlank id -> return (incomplete id)
    | EPartial (_, expr) -> return! eval state st expr
    | ELet (_id, lhs, rhs, body) ->
        let! rhs = eval state st rhs

        match rhs with
        // CLEANUP we should still preview the body
        // Usually fakevals get propagated when they're evaluated. However, if we don't use the value, we still want to propagate the errorrail here, so return it instead of evaling the body
        | DErrorRail v -> return rhs
        | _ ->
            let st = if lhs <> "" then st.Add(lhs, rhs) else st
            return! (eval state st body)
    | EString (_id, s) -> return (DStr(s.Normalize()))
    | EBool (_id, b) -> return DBool b
    | EInteger (_id, i) -> return DInt i
    | EFloat (_id, value) -> return DFloat value
    | ENull _id -> return DNull
    | ECharacter (_id, s) -> return DChar s
    | EList (_id, exprs) ->
        // We ignore incompletes but not error rail.
        // TODO: Other places where lists are created propagate incompletes
        // instead of ignoring, this is probably a mistake.
        let! results = Prelude.List.map_s (eval state st) exprs

        let filtered =
          List.filter (fun (dv : Dval) -> not (Dval.isIncomplete dv)) results
        // CLEANUP: why do we only find errorRail, and not errors. Seems like
        // a mistake
        match List.tryFind (fun (dv : Dval) -> Dval.isErrorRail dv) filtered with
        | Some er -> return er
        | None -> return (DList filtered)

    | EVariable (id, name) ->
        match (st.TryFind name, state.tracing.realOrPreview) with
        | None, Preview ->
            // CLEANUP this feels like giving an error would be an improvement
            // The trace is wrong/we have a bug -- we guarantee to users that
            // variables they can lookup have been bound. However, we
            // shouldn't crash out here when running analysis because it gives
            // a horrible user experience
            return incomplete id
        | None, Real ->
            return Dval.errSStr (sourceID id) $"There is no variable named: {name}"
        | Some other, _ -> return other
    | ERecord (id, pairs) ->
        let skipEmptyKeys =
          pairs
          |> List.choose
               (function
               | ("", e) -> None
               | k, e -> Some(k, e))
        // FSTODO: we actually want to stop on the first incomplete/error/etc, thing, not do them all.
        let! (resolved : List<string * Dval>) =
          List.map_s
            (fun (k, v) ->
              taskv {
                let! dv = eval state st v
                return (k, dv)
              })
            skipEmptyKeys

        return Dval.interpreterObj resolved
    | EApply (id, fnVal, exprs, inPipe, ster) ->
        let! fnVal = eval state st fnVal
        let! args = List.map_s (eval state st) exprs
        let! result = applyFn state id fnVal (Seq.toList args) inPipe ster

        do
          // Pipes have been removed at this point, but the editor still needs to
          // show a value for the pipe
          // CLEANUP: instead of saving this, fetch it in the right place (the
          // last pipe entry) in the editor
          match inPipe with
          | InPipe pipeID ->
              state.tracing.traceDval state.onExecutionPath pipeID result
          | NotInPipe -> ()

        return result
    | EFQFnValue (id, desc) -> return DFnVal(FnName(desc))
    | EFieldAccess (id, e, field) ->
        let! obj = eval state st e

        let result =
          match obj with
          | DObj o ->
              if field = "" then
                DIncomplete(sourceID id)
              else
                Map.tryFind field o |> Option.defaultValue DNull
          | DIncomplete _ -> obj
          | DErrorRail _ -> obj
          | DError _ -> obj // differs from ocaml, but produces an Error either way
          | x ->
              let actualType =
                match Dval.toType x with
                | TDB _ ->
                    "it's a Datastore. Use DB:: standard library functions to interact with Datastores"
                | tipe -> $"it's a {DvalRepr.typeToDeveloperReprV0 tipe}"

              DError(
                sourceID id,
                "Attempting to access a field of something that isn't a record or dict, ("
                + actualType
                + ")."
              )

        return! Value result
    | EFeatureFlag (id, cond, oldcode, newcode) ->
        // True gives newexpr, unlike in If statements
        //
        // In If statements, we use a false/null as false, and anything else is
        // true. But this won't work for feature flags. If statements are built
        // as you build you code, with no existing users. But feature flags are
        // created when you have users and don't want to break your code. As a
        // result, anything that isn't an explicitly signalling to use the new
        // code, should use the old code:
        // - errors should be ignored: use old code
        // - incompletes should be ignored: use old code
        // - errorrail should not be propaged: use old code
        // - values which are "truthy" in if statements are not truthy here:
        // imagine you are writing the FF cond and you get a list or object,
        // and you're about to do some other work on it. Should we immediately
        // start serving the new code to all your traffic? No. So only `true`
        // gets new code.

        let! cond =
          // under no circumstances should this cause code to fail
          try
            eval state st cond
          with e -> Value(DBool false)

        match cond with
        | DBool true ->
            // FSTODO
            (* preview st oldcode *)
            return! eval state st newcode
        // FSTODO
        | DIncomplete _
        | DErrorRail _
        | DError _ ->
            // FSTODO
            (* preview st newcode *)
            return! eval state st oldcode
        | _ ->
            // FSTODO
            (* preview st newcode *)
            return! eval state st oldcode

    // FSTODO
    | ELambda (_id, parameters, body) ->
        return DFnVal(Lambda { symtable = st; parameters = parameters; body = body })
    | EMatch (id, matchExpr, cases) ->
        return!
          (taskv {
            let hasMatched = ref false
            let matchResult = ref (incomplete id)

            let executeMatch
              (newDefs : (string * Dval) list)
              (traces : (id * Dval) list)
              (st : DvalMap)
              (expr : Expr)
              : TaskOrValue<unit> =
              taskv {
                // Once a pattern is matched, this function is called to execute its
                // `expr`. It tracks whether this is the first pattern to execute,
                // and calls preview if it is not. Handles calling trace on the
                // traces that have been collected by pattern matching.

                let newVars = Map.ofList newDefs

                let newSt = Map.union newVars st

                if !hasMatched then
                  // We matched, but we've already matched a pattern previously
                  List.iter
                    (fun (id, dval) -> state.tracing.traceDval false id dval)
                    traces

                  do! preview newSt expr
                  return ()
                else
                  List.iter
                    (fun (id, dval) ->
                      state.tracing.traceDval state.onExecutionPath id dval)
                    traces

                  hasMatched := true

                  let! result = eval state newSt expr
                  matchResult := result
                  return ()
              }

            let traceIncompletes traces =
              List.iter
                (fun (id, _) -> state.tracing.traceDval false id (incomplete id))
                traces

            let traceNonMatch
              (st : DvalMap)
              (expr : Expr)
              (traces : (id * Dval) list)
              (id : id)
              (value : Dval)
              : TaskOrValue<unit> =
              taskv {
                do! preview st expr
                traceIncompletes traces
                state.tracing.traceDval false id value
              }

            let rec matchAndExecute
              dv
              (builtUpTraces : (id * Dval) list)
              (pattern, expr)
              : TaskOrValue<unit> =
              // Compare `dv` to `pattern`, and execute the rhs `expr` of any
              // matches. Tracks whether a branch has already been executed and
              // will exceute later matches in preview mode.  Ensures all patterns
              // and branches are properly traced.  Recurse on partial matches
              // (constructors); builtUpTraces is the set of traces that have been
              // built up by recursing: they can only be matched when the pattern
              // is ready to match.
              match pattern with
              | PInteger (pid, i) ->
                  let v = DInt i

                  if v = dv then
                    executeMatch [] ((pid, v) :: builtUpTraces) st expr
                  else
                    traceNonMatch st expr builtUpTraces pid v
              | PBool (pid, bool) ->
                  let v = DBool bool

                  if v = dv then
                    executeMatch [] ((pid, v) :: builtUpTraces) st expr
                  else
                    traceNonMatch st expr builtUpTraces pid v
              | PCharacter (pid, c) ->
                  let v = DChar(c)

                  if v = dv then
                    executeMatch [] ((pid, v) :: builtUpTraces) st expr
                  else
                    traceNonMatch st expr builtUpTraces pid v
              | PString (pid, str) ->
                  let v = DStr(str)

                  if v = dv then
                    executeMatch [] ((pid, v) :: builtUpTraces) st expr
                  else
                    traceNonMatch st expr builtUpTraces pid v
              | PFloat (pid, v) ->
                  let v = DFloat v

                  if v = dv then
                    executeMatch [] ((pid, v) :: builtUpTraces) st expr
                  else
                    traceNonMatch st expr builtUpTraces pid v
              | PNull (pid) ->
                  let v = DNull

                  if v = dv then
                    executeMatch [] ((pid, v) :: builtUpTraces) st expr
                  else
                    traceNonMatch st expr builtUpTraces pid v
              | PVariable (pid, v) ->
                  // only matches allowed values
                  if Dval.isFake dv then
                    traceNonMatch st expr builtUpTraces pid dv
                  else
                    executeMatch [ (v, dv) ] ((pid, dv) :: builtUpTraces) st expr
              | PBlank (pid) ->
                  // never matches
                  traceNonMatch st expr builtUpTraces pid (incomplete pid)
              | PConstructor (pid, name, args) ->
                  (match (name, args, dv) with
                   | "Just", [ p ], DOption (Some v)
                   | "Ok", [ p ], DResult (Ok v)
                   | "Error", [ p ], DResult (Error v) ->
                       matchAndExecute v ((pid, dv) :: builtUpTraces) (p, expr)
                   | "Nothing", [], DOption None ->
                       executeMatch [] ((pid, dv) :: builtUpTraces) st expr
                   | "Nothing", [], _ ->
                       traceNonMatch st expr builtUpTraces pid (DOption None)
                   | _ ->
                       taskv {
                         let error =
                           if List.contains name [ "Just"; "Ok"; "Error"; "Nothing" ] then
                             incomplete pid
                           else
                             DError(sourceID pid, $"Invalid constructor: {name}")

                         do! traceNonMatch st expr builtUpTraces pid error
                         // Trace each argument too. TODO: recurse
                         List.iter
                           (fun pat ->
                             let id = Pattern.toID pat
                             state.tracing.traceDval false id (incomplete id))
                           args
                       })

            let! matchVal = eval state st matchExpr

            do!
              List.iter_s
                (fun (pattern, expr) -> matchAndExecute matchVal [] (pattern, expr))
                cases

            return !matchResult
           })

    | EIf (_id, cond, thenbody, elsebody) ->
        match! eval state st cond with
        | DBool (false)
        | DNull ->
            do! preview st thenbody
            return! eval state st elsebody
        | cond when Dval.isFake cond ->
            do! preview st thenbody
            do! preview st elsebody
            return cond
        // CLEANUP: I dont know why I made these always true
        | _ ->
            let! result = eval state st thenbody
            do! preview st elsebody
            return result
    | EConstructor (id, name, args) ->
        match (name, args) with
        | "Nothing", [] -> return DOption None
        | "Just", [ arg ] ->
            let! dv = (eval state st arg)
            return Dval.optionJust dv
        | "Ok", [ arg ] ->
            let! dv = eval state st arg
            return Dval.resultOk dv
        | "Error", [ arg ] ->
            let! dv = eval state st arg
            return Dval.resultError dv
        | name, _ ->
            return Dval.errSStr (sourceID id) $"Invalid name for constructor {name}"
  }

and eval (state : ExecutionState) (st : Symtable) (e : Expr) : DvalTask =
  taskv {
    let! (result : Dval) = eval' state st e
    state.tracing.traceDval state.onExecutionPath (Expr.toID e) result
    return result
  }

// Unwrap the dval, which we expect to be a function, and error if it's not
and applyFn
  (state : ExecutionState)
  (id : id)
  (fn : Dval)
  (args : List<Dval>)
  (isInPipe : IsInPipe)
  (ster : SendToRail)
  : DvalTask =
  taskv {
    let sourceID = SourceID(state.tlid, id)

    return!
      match fn, isInPipe with
      | DFnVal fnVal, _ -> applyFnVal state id fnVal args isInPipe ster
      // Incompletes are allowed in pipes
      | DIncomplete _, InPipe _ -> Value(Option.defaultValue fn (List.tryHead args))
      | other, _ ->
          Value(
            Dval.errSStr
              sourceID
              $"Expected a function value, got something else: {other}"
          )

  }

and applyFnVal
  (state : ExecutionState)
  (callerID : id)
  (fnVal : FnValImpl)
  (argList : List<Dval>)
  (isInPipe : IsInPipe)
  (ster : SendToRail)
  : DvalTask =
  match fnVal with
  | Lambda l -> executeLambda state l argList
  | FnName name -> callFn state callerID name argList isInPipe ster

and executeLambda
  (state : ExecutionState)
  (l : LambdaImpl)
  (args : List<Dval>)
  : DvalTask =

  // If one of the args is fake value used as a marker, return it instead of
  // executing. This is the same behaviour as in fn calls.
  let firstMarker = List.tryFind Dval.isFake args

  match firstMarker with
  | Some dv -> Value dv
  | None ->
      let parameters = List.map snd l.parameters
      // One of the reasons to take a separate list of params and args is to
      // provide this error message here. We don't have this information in
      // other places, and the alternative is just to provide incompletes
      // with no context
      if List.length l.parameters <> List.length args then
        Value(
          DError(
            SourceNone,
            $"Expected {List.length l.parameters} arguments, got {List.length args}"
          )
        )
      else
        List.iter
          (fun ((id, _), dv) -> state.tracing.traceDval state.onExecutionPath id dv)
          (List.zip l.parameters args)

        let paramSyms = List.zip parameters args |> Map
        // paramSyms is higher priority
        let newSymtable = Map.union paramSyms l.symtable

        eval state newSymtable l.body

and callFn
  (state : ExecutionState)
  (callerID : id)
  (desc : FQFnName.T)
  (argvals : Dval list)
  (isInPipe : IsInPipe)
  (sendToRail : SendToRail)
  : DvalTask =
  taskv {
    let sourceID id = SourceID(state.tlid, id) in

    let fn =
      match desc with
      | FQFnName.Stdlib std ->
          state.libraries.stdlib.TryFind desc |> Option.map builtInFnToFn
      | FQFnName.User name ->
          state.program.userFns.TryFind name |> Option.map userFnToFn
      | FQFnName.Package pkg ->
          state.libraries.packageFns.TryFind desc |> Option.map packageFnToFn

    match List.tryFind Dval.isErrorRail argvals with
    | Some er -> return er
    | None ->
        let! result =
          match fn with
          // Functions which aren't implemented in the client may have results
          // available, otherwise they just return incomplete.
          | None ->
              let fnRecord = (state.tlid, desc, callerID) in
              let fnResult = state.tracing.loadFnResult fnRecord argvals in
              // In the case of DB::query (and friends), we want to backfill
              // the lambda's livevalues, as the lambda was never actually
              // executed. We hack this is here as we have no idea what this
              // abstraction might look like in the future.
              if state.tracing.realOrPreview = Preview && desc.isDBQueryFn () then
                match argvals with
                | [ DDB dbname; DFnVal (Lambda b) ] ->
                    let sample =
                      match fnResult with
                      | Some (DList (sample :: _), _) -> sample
                      | _ ->
                          Map.find dbname state.program.dbs
                          |> (fun (db : DB.T) -> db.cols)
                          |> List.map
                               (fun (field, _) -> (field, DIncomplete SourceNone))
                          |> Map.ofList
                          |> DObj

                    ignore (executeLambda state b [ sample ])
                | _ -> ()

              match fnResult with
              | Some (result, _ts) -> Value(result)
              | _ -> Value(DIncomplete(sourceID callerID))
          | Some fn ->
              // equalize length
              let expectedLength = List.length fn.parameters in
              let actualLength = List.length argvals in

              if expectedLength = actualLength then
                let args =
                  fn.parameters
                  |> List.map2 (fun dv p -> (p.name, dv)) argvals
                  |> Map.ofList

                execFn state desc callerID fn args isInPipe
              else
                Value(
                  DError(
                    sourceID callerID,
                    $"{desc} has {expectedLength} parameters, but here was called"
                    + $" with {actualLength} arguments."
                  )
                )

        if sendToRail = Rail then
          match Dval.unwrapFromErrorRail result with
          | DOption (Some v) -> return v
          | DResult (Ok v) -> return v
          | DIncomplete _ as i -> return i
          | DError _ as e -> return e
          // There should only be DOptions and DResults here, but hypothetically we got
          // something else, they would go on the error rail too.
          | other -> return DErrorRail other
        else
          return result
  }


and execFn
  (state : ExecutionState)
  (fnDesc : FQFnName.T)
  (id : id)
  (fn : Fn)
  (args : DvalMap)
  (isInPipe : IsInPipe)
  : DvalTask =
  taskv {
    let sourceID = SourceID(state.tlid, id) in

    let typeErrorOrValue userTypes result =
      (* https://www.notion.so/darklang/What-should-happen-when-the-return-type-is-wrong-533f274f94754549867fefc554f9f4e3 *)
      match TypeChecker.checkFunctionReturnType userTypes fn result with
      | Ok () -> result
      | Error errs ->
          DError(
            sourceID,
            $"Type error(s) in return type: {TypeChecker.Error.listToString errs}"
          )

    if state.tracing.realOrPreview = Preview
       && not state.onExecutionPath
       && Set.contains fnDesc state.callstack then
      // Don't recurse (including transitively!) when previewing unexecuted paths
      // in the editor. If we do, we'll recurse forever and blow the stack. *)
      return DIncomplete(SourceID(state.tlid, id))
    else
      let state =
        { state with
            executingFnName = Some fnDesc
            callstack = Set.add fnDesc state.callstack }

      let arglist =
        fn.parameters
        |> List.map (fun (p : Param) -> p.name)
        |> List.choose (fun key -> Map.tryFind key args)

      let argsWithGlobals = withGlobals state args

      let fnRecord = (state.tlid, fnDesc, id) in

      let badArg =
        List.tryFind
          (function
          | DError _ when fnDesc = FQFnName.stdlibFqName "Bool" "isError" 0 -> false
          | DError _
          | DIncomplete _ -> true
          | _ -> false)
          arglist

      match badArg, isInPipe with
      | Some (DIncomplete src), InPipe _ ->
          // That is, unless it's an incomplete in a pipe. In a pipe, we treat
          // the entire expression as a blank, and skip it, returning the input
          // (first) value to be piped into the next statement instead. *)
          return List.head arglist
      | Some (DIncomplete src), _ -> return DIncomplete src
      | Some (DError (src, _) as err), _ ->
          // CLEANUP: kept old error to make testing easier, but this is an
          // easy and safe change to make
          return DError(src, "Fn called with an error as an argument")
      // return err
      | _ ->
          try
            match fn.fn with
            | StdLib f ->
                if state.tracing.realOrPreview = Preview && fn.previewable = Pure then
                  match state.tracing.loadFnResult fnRecord arglist with
                  | Some (result, _ts) -> return result
                  | None -> return DIncomplete sourceID
                else
                  let! result = f (state, arglist)
                  // there's no point storing data we'll never ask for
                  let! () =
                    if fn.previewable <> Pure then
                      state.tracing.storeFnResult fnRecord arglist result
                    else
                      task { return () }

                  return result
            | PackageFunction (tlid, body) ->
                // This is similar to InProcess but also has elements of UserCreated.
                match TypeChecker.checkFunctionCall Map.empty fn args with
                | Ok () ->
                    let! result =
                      match (state.tracing.realOrPreview,
                             state.tracing.loadFnResult fnRecord arglist) with
                      | Preview, Some (result, _ts) ->
                          Value(Dval.unwrapFromErrorRail result)
                      | Preview, None when fn.previewable <> Pure ->
                          Value(DIncomplete sourceID)
                      | _ ->
                          taskv {
                            // It's okay to execute user functions in both Preview and
                            // Real contexts, But in Preview we might not have all the
                            // data we need

                            // TODO: We don't munge `state.tlid` like we do in
                            // UserCreated, which means there might be `id`
                            // collisions between AST nodes. Munging `state.tlid`
                            // would not save us from tlid collisions either.
                            // tl;dr, executing a package function may result in
                            // trace data being associated with the wrong
                            // handler/call site.
                            let! result = eval state argsWithGlobals body

                            do! state.tracing.storeFnResult fnRecord arglist result

                            return
                              result
                              |> Dval.unwrapFromErrorRail
                              |> typeErrorOrValue Map.empty
                          }
                    // there's no point storing data we'll never ask for *)
                    let! () =
                      if fn.previewable <> Pure then
                        state.tracing.storeFnResult fnRecord arglist result
                      else
                        task { return () }

                    return result
                | Error errs ->
                    return
                      DError(
                        sourceID,
                        ("Type error(s) in function parameters: "
                         + TypeChecker.Error.listToString errs)
                      )
            | UserFunction (tlid, body) ->
                match TypeChecker.checkFunctionCall state.program.userTypes fn args with
                | Ok () ->
                    state.tracing.traceTLID tlid
                    // Don't execute user functions if it's preview mode and we have a result
                    match (state.tracing.realOrPreview,
                           state.tracing.loadFnResult fnRecord arglist) with
                    | Preview, Some (result, _ts) ->
                        return Dval.unwrapFromErrorRail result
                    | _ ->
                        // It's okay to execute user functions in both Preview and Real contexts,
                        // But in Preview we might not have all the data we need
                        do! state.tracing.storeFnArguments tlid args

                        let state = { state with tlid = tlid }
                        let! result = eval state argsWithGlobals body
                        do! state.tracing.storeFnResult fnRecord arglist result

                        return
                          result
                          |> Dval.unwrapFromErrorRail
                          |> typeErrorOrValue state.program.userTypes
                | Error errs ->
                    return
                      DError(
                        sourceID,
                        ("Type error(s) in function parameters: "
                         + TypeChecker.Error.listToString errs)
                      )
          with
          | Errors.FakeValFoundInQuery dv -> return dv
          | Errors.DBQueryException e ->
              return (Dval.errStr (Errors.queryCompilerErrorTemplate + e))
          | Errors.StdlibException (Errors.StringError msg) ->
              return (Dval.errSStr sourceID msg)
          | Errors.StdlibException Errors.IncorrectArgs ->
              let paramLength = List.length fn.parameters
              let argLength = List.length arglist

              if paramLength <> argLength then
                return
                  (Dval.errSStr
                    sourceID
                    ($"{fn.name} has {paramLength} parameters,"
                     + $" but here was called with {argLength} arguments."))

              else
                let invalid =
                  List.zip fn.parameters arglist
                  |> List.filter
                       (fun (p, a) -> Dval.toType a <> p.typ && not (p.typ.isAny ()))

                match invalid with
                | [] ->
                    return (Dval.errSStr sourceID $"unknown error calling {fn.name}")
                | (p, actual) :: _ ->
                    let msg = Errors.incorrectArgsMsg (fn.name) p actual
                    return (Dval.errSStr sourceID msg)
          | Errors.StdlibException Errors.FunctionRemoved ->
              return (Dval.errSStr sourceID $"{fn.name} was removed from Dark")
          | Errors.StdlibException (Errors.FakeDvalFound dv) -> return dv
          // After the rethrow, this gets eventually caught then shown to the
          // user as a Dark Internal Exception. It's an internal exception
          // because we didn't anticipate the problem, give it a nice error
          // message, etc. It'll appear in Rollbar as "Unknown Err". To remedy
          // this, give it a nice exception via RT.error. *)
          // FSTODO: the message above needs to be handled
          | e -> return (Dval.errSStr sourceID (toString e))

  }
