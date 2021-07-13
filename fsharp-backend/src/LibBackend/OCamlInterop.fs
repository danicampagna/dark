module LibBackend.OCamlInterop

// Interoperation functions with OCaml.

// Programs are stored using an OCaml-only serialization format, so we have to
// call OCaml code to fetch it and save it.  We send binary code which we get
// from the DB, convert it to OCaml types, then json convert it to get it back
// into F#. At that point we convert it to these types, and potentially convert
// it to the runtime types to run it.

// We also use these types to convert to the types the API uses, which are
// typically direct deserializations of these types.

open FSharpPlus

open System.Threading.Tasks
open FSharp.Control.Tasks

open Prelude
open Tablecloth

module PT = LibExecution.ProgramTypes
module RT = LibExecution.RuntimeTypes
module OCamlTypes = LibExecution.OCamlTypes
module Convert = LibExecution.OCamlTypes.Convert

let digest () = "0e91e490041f06fae012f850231eb6ab"


// ----------------
// Getting values from OCaml
// ----------------
// FSTODO: this is not the right way I think
let client = new System.Net.Http.HttpClient()

let legacyReq
  (endpoint : string)
  (data : byte array)
  : Task<System.Net.Http.HttpContent> =
  task {
    let url = $"http://localhost:{LibService.Config.legacyServerPort}/{endpoint}"

    use message =
      new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, url)

    message.Content <- new System.Net.Http.ByteArrayContent(data)

    let! response = client.SendAsync(message)

    if response.StatusCode = System.Net.HttpStatusCode.OK then
      ()
    else if response.StatusCode = System.Net.HttpStatusCode.BadRequest then
      // This is how errors are reported
      let! content = response.Content.ReadAsStringAsync()
      failwith content
    else
      let! content = response.Content.ReadAsStringAsync()
      failwith $"not a 200 response to {endpoint}: {response.StatusCode}, {content}"

    return response.Content
  }

let legacyStringReq (endpoint : string) (data : byte array) : Task<string> =
  task {
    let! content = legacyReq endpoint data
    return! content.ReadAsStringAsync()
  }

let legacyBytesReq (endpoint : string) (data : byte array) : Task<byte array> =
  task {
    let! content = legacyReq endpoint data
    return! content.ReadAsByteArrayAsync()
  }

let serialize (v : 'a) : byte array = v |> Json.OCamlCompatible.serialize |> toBytes

let stringToBytesReq (endpoint : string) (str : string) : Task<byte array> =
  str |> toBytes |> legacyBytesReq endpoint

let bytesToStringReq (endpoint : string) (data : byte array) : Task<string> =
  data |> legacyStringReq endpoint

let stringToStringReq (endpoint : string) (str : string) : Task<string> =
  str |> toBytes |> legacyStringReq endpoint

let stringToDvalReq (endpoint : string) (str : string) : Task<RT.Dval> =
  str
  |> toBytes
  |> legacyStringReq endpoint
  |> Task.map Json.OCamlCompatible.deserialize<OCamlTypes.RuntimeT.dval>
  |> Task.map Convert.ocamlDval2rt

let dvalToStringReq (endpoint : string) (dv : RT.Dval) : Task<string> =
  dv |> Convert.rt2ocamlDval |> serialize |> legacyStringReq endpoint

let dvalListToStringReq (endpoint : string) (l : List<RT.Dval>) : Task<string> =
  l |> List.map Convert.rt2ocamlDval |> serialize |> legacyStringReq endpoint


// Binary deserialization functions

let oplistOfBinary (data : byte array) : Task<PT.Oplist> =
  data
  |> bytesToStringReq "bs/oplist_bin2json"
  |> Task.map
       Json.OCamlCompatible.deserialize<OCamlTypes.oplist<OCamlTypes.RuntimeT.fluidExpr>>
  |> Task.map Convert.ocamlOplist2PT

let oplistToBinary (oplist : PT.Oplist) : Task<byte array> =
  oplist
  |> Convert.pt2ocamlOplist
  |> Json.OCamlCompatible.serialize
  |> stringToBytesReq "bs/oplist_json2bin"

let exprTLIDPairOfCachedBinary (data : byte array) : Task<PT.Expr * tlid> =
  data
  |> bytesToStringReq "bs/expr_tlid_pair_bin2json"
  |> Task.map
       Json.OCamlCompatible.deserialize<OCamlTypes.RuntimeT.fluidExpr * OCamlTypes.tlid>
  |> Task.map Convert.ocamlexprTLIDPair2PT

let exprTLIDPairToCachedBinary ((expr, tlid) : (PT.Expr * tlid)) : Task<byte array> =
  (expr, tlid)
  |> Convert.pt2ocamlexprTLIDPair
  |> Json.OCamlCompatible.serialize
  |> stringToBytesReq "bs/expr_tlid_pair_json2bin"

let handlerBin2Json (data : byte array) (pos : pos) : Task<PT.Handler.T> =
  data
  |> bytesToStringReq "bs/handler_bin2json"
  |> Task.map
       Json.OCamlCompatible.deserialize<OCamlTypes.RuntimeT.HandlerT.handler<OCamlTypes.RuntimeT.fluidExpr>>
  |> Task.map (Convert.ocamlHandler2PT pos)

let dbBin2Json (data : byte array) (pos : pos) : Task<PT.DB.T> =
  data
  |> bytesToStringReq "bs/db_bin2json"
  |> Task.map
       Json.OCamlCompatible.deserialize<OCamlTypes.RuntimeT.DbT.db<OCamlTypes.RuntimeT.fluidExpr>>
  |> Task.map (Convert.ocamlDB2PT pos)

let userFnBin2Json (data : byte array) : Task<PT.UserFunction.T> =
  data
  |> bytesToStringReq "bs/user_fn_bin2json"
  |> Task.map
       Json.OCamlCompatible.deserialize<OCamlTypes.RuntimeT.user_fn<OCamlTypes.RuntimeT.fluidExpr>>
  |> Task.map Convert.ocamlUserFunction2PT

let userTypeBin2Json (data : byte array) : Task<PT.UserType.T> =
  data
  |> bytesToStringReq "bs/user_tipe_bin2json"
  |> Task.map Json.OCamlCompatible.deserialize<OCamlTypes.RuntimeT.user_tipe>
  |> Task.map Convert.ocamlUserType2PT


let toplevelOfCachedBinary
  ((data, pos) : (byte array * string option))
  : Task<PT.Toplevel> =
  let pos =
    pos
    |> Option.map Json.OCamlCompatible.deserialize<pos>
    |> Option.unwrap { x = 0; y = 0 }

  task {
    try
      return! handlerBin2Json data pos |> Task.map PT.TLHandler
    with e1 ->
      try
        return! dbBin2Json data pos |> Task.map PT.TLDB
      with e2 ->
        try
          return! userFnBin2Json data |> Task.map PT.TLFunction
        with e3 ->
          try
            return! userTypeBin2Json data |> Task.map PT.TLType
          with e4 ->
            failwith
              $"could not parse binary toplevel {e1}\n\n{e2}\n\n{e3}\n\n{e4}\n\n"

            let (ids : PT.Handler.ids) =
              { moduleID = id 0; nameID = id 0; modifierID = id 0 }

            return
              PT.TLHandler
                { tlid = id 0
                  pos = pos
                  ast = PT.EBlank(id 0)
                  spec = PT.Handler.REPL("somename", ids) }
  }


let handlerJson2Bin (h : PT.Handler.T) : Task<byte array> =
  h
  |> Convert.pt2ocamlHandler
  |> Json.OCamlCompatible.serialize
  |> stringToBytesReq "bs/handler_json2bin"

let dbJson2Bin (db : PT.DB.T) : Task<byte array> =
  db
  |> Convert.pt2ocamlDB
  |> Json.OCamlCompatible.serialize
  |> stringToBytesReq "bs/db_json2bin"


let userFnJson2Bin (userFn : PT.UserFunction.T) : Task<byte array> =
  userFn
  |> Convert.pt2ocamlUserFunction
  |> Json.OCamlCompatible.serialize
  |> stringToBytesReq "bs/user_fn_json2bin"


let userTypeJson2Bin (userType : PT.UserType.T) : Task<byte array> =
  userType
  |> Convert.pt2ocamlUserType
  |> Json.OCamlCompatible.serialize
  |> stringToBytesReq "bs/user_tipe_json2bin"

let toplevelToCachedBinary (toplevel : PT.Toplevel) : Task<byte array> =
  match toplevel with
  | PT.TLHandler h -> handlerJson2Bin h
  | PT.TLDB db -> dbJson2Bin db
  | PT.TLFunction f -> userFnJson2Bin f
  | PT.TLType t -> userTypeJson2Bin t


// ---------------------------
// These are only here for fuzzing. We should not be fetching dvals via the
// OCaml runtime, but always via HTTP or via the DB.
// ---------------------------
let ofInternalQueryableV0 (str : string) : Task<RT.Dval> =
  stringToDvalReq "fuzzing/of_internal_queryable_v0" str

let ofInternalQueryableV1 (str : string) : Task<RT.Dval> =
  stringToDvalReq "fuzzing/of_internal_queryable_v1" str

let ofInternalRoundtrippableV0 (str : string) : Task<RT.Dval> =
  stringToDvalReq "fuzzing/of_internal_roundtrippable_v0" str

let ofUnknownJsonV1 (str : string) : Task<RT.Dval> =
  stringToDvalReq "fuzzing/of_unknown_json_v1" str

let toDeveloperRepr (dv : RT.Dval) : Task<string> =
  dvalToStringReq "fuzzing/to_developer_repr_v0" dv

let toEnduserReadableTextV0 (dv : RT.Dval) : Task<string> =
  dvalToStringReq "fuzzing/to_enduser_readable_text_v0" dv

let toHashableRepr (dv : RT.Dval) : Task<string> =
  dvalToStringReq "fuzzing/to_hashable_repr" dv

let toInternalQueryableV0 (dv : RT.Dval) : Task<string> =
  dvalToStringReq "fuzzing/to_internal_queryable_v0" dv

let toInternalQueryableV1 (dv : RT.Dval) : Task<string> =
  dvalToStringReq "fuzzing/to_internal_queryable_v1" dv

let toInternalRoundtrippableV0 (dv : RT.Dval) : Task<string> =
  dvalToStringReq "fuzzing/to_internal_roundtrippable_v0" dv

let toPrettyMachineJsonV1 (dv : RT.Dval) : Task<string> =
  dvalToStringReq "fuzzing/to_pretty_machine_json_v1" dv

let toUrlString (dv : RT.Dval) : Task<string> =
  dvalToStringReq "fuzzing/to_url_string" dv

let hashV0 (l : List<RT.Dval>) : Task<string> =
  dvalListToStringReq "fuzzing/hash_v0" l

let hashV1 (l : List<RT.Dval>) : Task<string> =
  dvalListToStringReq "fuzzing/hash_v1" l

let execute
  (ownerID : UserID)
  (canvasID : CanvasID)
  (program : PT.Expr)
  (symtable : Map<string, RT.Dval>)
  (dbs : List<PT.DB.T>)
  (fns : List<PT.UserFunction.T>)
  : Task<RT.Dval> =
  let program = Convert.pt2ocamlExpr program

  let args =
    symtable |> Map.toList |> List.map (fun (k, dv) -> (k, Convert.rt2ocamlDval dv))

  let dbs = List.map Convert.pt2ocamlDB dbs
  let fns = List.map Convert.pt2ocamlUserFunction fns

  let str =
    Json.OCamlCompatible.serialize ((ownerID, canvasID, program, args, dbs, fns))

  task {
    try
      let! result = stringToDvalReq "execute" str
      return result
    with e -> return (RT.DError(RT.SourceNone, e.Message))
  }
