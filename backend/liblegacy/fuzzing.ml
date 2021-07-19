open Core_kernel
open Libexecution

let of_internal_queryable_v0 (str : string) : string =
  let dval = Dval.of_internal_queryable_v0 str in
  dval |> Types.RuntimeT.dval_to_yojson |> Yojson.Safe.to_string


let of_internal_queryable_v1 (str : string) : string =
  let dval = Dval.of_internal_queryable_v1 str in
  dval |> Types.RuntimeT.dval_to_yojson |> Yojson.Safe.to_string


let of_internal_roundtrippable_v0 (str : string) : string =
  let dval = Dval.of_internal_roundtrippable_v0 str in
  dval |> Types.RuntimeT.dval_to_yojson |> Yojson.Safe.to_string


let of_unknown_json_v1 (str : string) : string =
  let dval = Dval.of_unknown_json_v1 str in
  dval |> Types.RuntimeT.dval_to_yojson |> Yojson.Safe.to_string


let to_developer_repr_v0 (json : string) : string =
  let dval =
    json
    |> Yojson.Safe.from_string
    |> Types.RuntimeT.dval_of_yojson
    |> Result.ok_or_failwith
  in
  Dval.to_developer_repr_v0 dval


let to_enduser_readable_text_v0 (json : string) : string =
  let dval =
    json
    |> Yojson.Safe.from_string
    |> Types.RuntimeT.dval_of_yojson
    |> Result.ok_or_failwith
  in
  Dval.to_enduser_readable_text_v0 dval


let to_hashable_repr (json : string) : string =
  let dval =
    json
    |> Yojson.Safe.from_string
    |> Types.RuntimeT.dval_of_yojson
    |> Result.ok_or_failwith
  in
  Dval.fuzzing_to_hashable_repr dval


let to_internal_queryable_v0 (json : string) : string =
  let dval =
    json
    |> Yojson.Safe.from_string
    |> Types.RuntimeT.dval_of_yojson
    |> Result.ok_or_failwith
  in
  Dval.to_internal_queryable_v0 dval


let to_internal_queryable_v1 (json : string) : string =
  let dval =
    json
    |> Yojson.Safe.from_string
    |> Types.RuntimeT.dval_of_yojson
    |> Result.ok_or_failwith
  in
  match dval with
  | DObj dvm ->
      Dval.to_internal_queryable_v1 dvm
  | _ ->
      failwith "Incorrect should be DObj"


let to_internal_roundtrippable_v0 (json : string) : string =
  let dval =
    json
    |> Yojson.Safe.from_string
    |> Types.RuntimeT.dval_of_yojson
    |> Result.ok_or_failwith
  in
  Dval.to_internal_roundtrippable_v0 dval


let to_pretty_machine_json_v1 (json : string) : string =
  let dval =
    json
    |> Yojson.Safe.from_string
    |> Types.RuntimeT.dval_of_yojson
    |> Result.ok_or_failwith
  in
  Dval.to_pretty_machine_json_v1 dval


let to_url_string (json : string) : string =
  let dval =
    json
    |> Yojson.Safe.from_string
    |> Types.RuntimeT.dval_of_yojson
    |> Result.ok_or_failwith
  in
  Dval.to_url_string_exn dval


let hash_v0 (json : string) : string =
  let dvallist =
    json
    |> Yojson.Safe.from_string
    |> Types.RuntimeT.dval_list_of_yojson
    |> Result.ok_or_failwith
  in
  Dval.hash 0 dvallist


let hash_v1 (json : string) : string =
  let dvallist =
    json
    |> Yojson.Safe.from_string
    |> Types.RuntimeT.dval_list_of_yojson
    |> Result.ok_or_failwith
  in
  Dval.hash 1 dvallist


(* ---------------------- *)
(* Below this is fuzzing execution *)
(* ---------------------- *)
let sideEffectCount : int ref = ref 0

let fns : Types.RuntimeT.fn list =
  [ { prefix_names = ["Test::errorRailNothing"]
    ; infix_names = []
    ; parameters = []
    ; return_type = TOption
    ; description = "Return an errorRail wrapping nothing."
    ; func =
        InProcess
          (function
          | state, [] -> DErrorRail (DOption OptNothing) | args -> Lib.fail args)
    ; preview_safety = Safe
    ; deprecated = false }
  ; { prefix_names = ["Test::typeError"]
    ; infix_names = []
    ; parameters = [Lib.par "errorString" TStr]
    ; return_type = TInt
    ; description = "Return a value representing a type error"
    ; func =
        InProcess
          (function
          | state, [DStr errorString] ->
              DError (SourceNone, Unicode_string.to_string errorString)
          | args ->
              Lib.fail args)
    ; preview_safety = Safe
    ; deprecated = false }
  ; { prefix_names = ["Test::sqlError"]
    ; infix_names = []
    ; parameters = [Lib.par "errorString" TStr]
    ; return_type = TInt
    ; description =
        "Return a value that matches errors thrown by the SqlCompiler"
    ; func =
        InProcess
          (function
          | state, [DStr errorString] ->
              let msg = "TODO: we don't support sqlError yet" in
              DError (SourceNone, msg)
          | args ->
              Lib.fail args)
    ; preview_safety = Safe
    ; deprecated = false }
  ; { prefix_names = ["Test::nan"]
    ; infix_names = []
    ; parameters = []
    ; return_type = TFloat
    ; description = "Return a NaN"
    ; func =
        InProcess (function _, [] -> DFloat Float.nan | args -> Lib.fail args)
    ; preview_safety = Safe
    ; deprecated = false }
  ; { prefix_names = ["Test::infinity"]
    ; infix_names = []
    ; parameters = []
    ; return_type = TFloat
    ; description = "Returns positive infitity"
    ; func =
        InProcess
          (function _, [] -> DFloat Float.infinity | args -> Lib.fail args)
    ; preview_safety = Safe
    ; deprecated = false }
  ; { prefix_names = ["Test::negativeInfinity"]
    ; infix_names = []
    ; parameters = []
    ; return_type = TFloat
    ; description = "Returns negative infitity"
    ; func =
        InProcess
          (function
          | _, [] -> DFloat Float.neg_infinity | args -> Lib.fail args)
    ; preview_safety = Safe
    ; deprecated = false }
  ; { prefix_names = ["Test::incrementSideEffectCounter"]
    ; infix_names = []
    ; parameters = [Lib.par "passThru" TAny]
    ; return_type = TAny
    ; description =
        "Increases the side effect counter by one, to test real-world side-effects. Returns its argument."
    ; func =
        InProcess
          (function
          | state, [arg] ->
              sideEffectCount := !sideEffectCount + 1 ;
              arg
          | args ->
              Lib.fail args)
    ; preview_safety = Safe
    ; deprecated = false }
  ; { prefix_names = ["Test::sideEffectCount"]
    ; infix_names = []
    ; parameters = []
    ; return_type = TInt
    ; description = "Return the value of the side-effect counter"
    ; func =
        InProcess
          (function
          | state, [] -> Dval.dint !sideEffectCount | args -> Lib.fail args)
    ; preview_safety = Safe
    ; deprecated = false }
  ; { prefix_names = ["Test::inspect"]
    ; infix_names = []
    ; parameters = [Lib.par "var" TAny; Lib.par "msg" TStr]
    ; return_type = TAny
    ; description = "Prints the value into stdout"
    ; func =
        InProcess
          (function
          | state, [v; DStr msg] ->
              let msg = Unicode_string.to_string msg in
              Libcommon.Log.inspecT msg v ;
              v
          | args ->
              Lib.fail args)
    ; preview_safety = Safe
    ; deprecated = false }
  ; { prefix_names = ["Test::justWithTypeError"]
    ; infix_names = []
    ; parameters = [Lib.par "msg" TStr]
    ; return_type = TOption
    ; description = "Returns a DError in a Just"
    ; func =
        InProcess
          (function
          | state, [DStr msg] ->
              let msg = Unicode_string.to_string msg in
              DOption (OptJust (DError (SourceNone, msg)))
          | args ->
              Lib.fail args)
    ; preview_safety = Safe
    ; deprecated = false }
  ; { prefix_names = ["Test::okWithTypeError"]
    ; infix_names = []
    ; parameters = [Lib.par "msg" TStr]
    ; return_type = TResult
    ; description = "Returns a DError in an Ok"
    ; func =
        InProcess
          (function
          | state, [DStr msg] ->
              let msg = Unicode_string.to_string msg in
              DResult (ResOk (DError (SourceNone, msg)))
          | args ->
              Lib.fail args)
    ; preview_safety = Safe
    ; deprecated = false }
  ; { prefix_names = ["Test::errorWithTypeError"]
    ; infix_names = []
    ; parameters = [Lib.par "msg" TStr]
    ; return_type = TResult
    ; description = "Returns a DError in a Just"
    ; func =
        InProcess
          (function
          | state, [DStr msg] ->
              let msg = Unicode_string.to_string msg in
              DResult (ResError (DError (SourceNone, msg)))
          | args ->
              Lib.fail args)
    ; preview_safety = Safe
    ; deprecated = false } ]


let exec_state : Types.RuntimeT.exec_state =
  { tlid = Types.id_of_int 7
  ; callstack = Tc.StrSet.empty
  ; account_id = Uuidm.v `V4
  ; canvas_id = Uuidm.v `V4
  ; user_fns = []
  ; user_tipes = []
  ; package_fns = []
  ; secrets = []
  ; fail_fn = None
  ; executing_fnname = ""
  ; dbs = []
  ; execution_id = Types.id_of_int 8
  ; trace = (fun ~on_execution_path _ _ -> ())
  ; trace_tlid = (fun _ -> ())
  ; on_execution_path = true
  ; exec = Ast.exec
  ; context = Real
  ; load_fn_result = Execution.load_no_results
  ; store_fn_result = Execution.store_no_results
  ; load_fn_arguments = Execution.load_no_arguments
  ; store_fn_arguments = Execution.store_no_arguments }


type uuid = Uuidm.t

let uuid_of_yojson json : (uuid, string) result =
  match json with
  | `String v ->
    ( match Uuidm.of_string v with
    | Some v ->
        Ok v
    | None ->
        Error ("uuid could not be read: " ^ v) )
  | _ ->
      Error "not string returned as uuid"


type execute_type =
  uuid
  * uuid
  * Types.fluid_expr
  * (string * Types.RuntimeT.dval) list
  * Types.RuntimeT.DbT.db list
  * Types.RuntimeT.user_fn list
[@@deriving of_yojson]

let execute (json : string) : string =
  try
    (* FSTODO: no longer a good reason not to test the rest of them *)
    let account_id, canvas_id, program, args, dbs, user_fns =
      json
      |> Yojson.Safe.from_string
      |> execute_type_of_yojson
      |> Result.ok_or_failwith
    in
    let exec_state = {exec_state with account_id; canvas_id; dbs; user_fns} in
    Ast.execute_ast ~input_vars:args ~state:exec_state program
    |> Types.RuntimeT.dval_to_yojson
    |> Yojson.Safe.to_string
  with e ->
    print_endline (Exception.to_string e) ;
    Libexecution.Exception.reraise e
