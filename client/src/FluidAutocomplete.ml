open Tc
open Types
open Prelude

(* Dark *)
module P = Pointer
module RT = Runtime
module TL = Toplevel
module B = Blank
module Regex = Util.Regex

type autocomplete = fluidAutocompleteState

type tokenInfo = fluidTokenInfo

(* ---------------------------- *)
(* Focus *)
(* ---------------------------- *)
(* show the prev 5 *)
(* obvi this should use getClientBoundingBox, but that's tough in Elm *)
let height (i : int) : int = if i < 4 then 0 else 14 * (i - 4)

let focusItem (i : int) : msg Tea.Cmd.t =
  Tea_task.attempt
    (fun _ -> IgnoreMsg)
    (Tea_task.nativeBinding (fun _ ->
         let open Webapi.Dom in
         match Document.getElementById "autocomplete-holder" document with
         | Some el ->
             Element.setScrollTop el (i |> height |> float_of_int)
         | None ->
             () ))


(* ---------------------------- *)
(* display *)
(* ---------------------------- *)
let asName (aci : autocompleteItem) : string =
  match aci with
  | ACFunction {fnName} ->
      fnName
  | ACField name ->
      name
  | ACVariable name ->
      name
  | ACCommand command ->
      ":" ^ command.commandName
  | ACLiteral lit ->
      lit
  | ACOmniAction ac ->
    ( match ac with
    | NewDB maybeName ->
      ( match maybeName with
      | Some name ->
          "New DB named " ^ name
      | None ->
          "New DB" )
    | NewHandler maybeName ->
      ( match maybeName with
      | Some name ->
          "New event handler named " ^ name
      | None ->
          "New event handler" )
    | NewFunction maybeName ->
      ( match maybeName with
      | Some name ->
          "New function named " ^ name
      | None ->
          "New function" )
    | NewHTTPHandler maybeName ->
      ( match maybeName with
      | Some name ->
          "New HTTP handler named " ^ name
      | None ->
          "New HTTP handler" )
    | NewEventSpace name ->
        "New handler in the " ^ name ^ " space"
    | Goto (_, _, desc) ->
        desc )
  | ACConstructorName name ->
      name
  | ACKeyword k ->
    ( match k with
    | KLet ->
        "let"
    | KIf ->
        "if"
    | KLambda ->
        "lambda"
    | KMatch ->
        "match" )
  | ACHTTPModifier name ->
      name
  | ACEventName name ->
      name
  | ACCronTiming timing ->
      timing
  | ACEventSpace space ->
      space
  | ACDBColType tipe ->
      tipe
  | ACParamTipe tipe ->
      RT.tipe2str tipe
  | ACDBName name ->
      name
  | ACExtra _ ->
      ""
  | ACTypeFieldTipe tipe ->
      RT.tipe2str tipe


let asTypeString (item : autocompleteItem) : string =
  match item with
  | ACFunction f ->
      f.fnParameters
      |> List.map ~f:(fun x -> x.paramTipe)
      |> List.map ~f:RT.tipe2str
      |> String.join ~sep:", "
      |> fun s -> "(" ^ s ^ ") ->  " ^ RT.tipe2str f.fnReturnTipe
  | ACField _ ->
      "field"
  | ACVariable _ ->
      "variable"
  | ACCommand _ ->
      ""
  | ACConstructorName name ->
      if name = "Just"
      then "(any) -> option"
      else if name = "Nothing"
      then "option"
      else if name = "Ok" || name = "Error"
      then "(any) -> result"
      else ""
  | ACLiteral lit ->
      let tipe =
        lit
        |> Decoders.parseDvalLiteral
        |> Option.withDefault ~default:DIncomplete
        |> RT.typeOf
        |> RT.tipe2str
      in
      tipe ^ " literal"
  | ACOmniAction _ ->
      ""
  | ACKeyword _ ->
      "keyword"
  | ACHTTPModifier _ ->
      "method"
  | ACEventName _ ->
      "event name"
  | ACCronTiming _ ->
      "interval"
  | ACEventSpace _ ->
      "event space"
  | ACDBColType _ ->
      "type"
  | ACParamTipe _ ->
      "param type"
  | ACDBName _ ->
      "name"
  | ACExtra _ ->
      ""
  | ACTypeFieldTipe tipe ->
    ( match tipe with
    | TUserType (_, v) ->
        "version " ^ string_of_int v
    | _ ->
        "builtin" )


let asString (aci : autocompleteItem) : string = asName aci ^ asTypeString aci

(* ---------------------------- *)
(* External: utils *)
(* ---------------------------- *)
let findFunction (a : autocomplete) (name : string) : function_ option =
  List.find ~f:(fun f -> f.fnName = name) a.functions


let allCompletions (a : autocomplete) : autocompleteItem list =
  a.completions @ a.invalidCompletions


let highlighted (a : autocomplete) : autocompleteItem option =
  Option.andThen a.index ~f:(fun index -> List.getAt ~index (allCompletions a))


let rec containsOrdered (needle : string) (haystack : string) : bool =
  match String.uncons needle with
  | Some (c, newneedle) ->
      let char = String.fromChar c in
      String.contains ~substring:char haystack
      && containsOrdered
           newneedle
           ( haystack
           |> String.split ~on:char
           |> List.drop ~count:1
           |> String.join ~sep:char )
  | None ->
      true


let dvalFields (dv : dval) : string list =
  match dv with DObj dict -> StrDict.keys dict | _ -> []


let findCompatibleThreadParam ({fnParameters} : function_) (tipe : tipe) :
    parameter option =
  fnParameters
  |> List.head
  |> Option.andThen ~f:(fun fst ->
         if RT.isCompatible fst.paramTipe tipe then Some fst else None )


let findParamByType ({fnParameters} : function_) (tipe : tipe) :
    parameter option =
  fnParameters |> List.find ~f:(fun p -> RT.isCompatible p.paramTipe tipe)


let dvalForTarget (m : model) (tl : toplevel) (ti : tokenInfo) : dval option =
  let ast = tl |> TL.asHandler |> Option.map ~f:(fun x -> x.ast) in
  match ast with
  | Some ast ->
      let id = tid ti.token in
      AST.find id ast
      |> Option.andThen ~f:(fun pd -> AST.getValueParent pd ast)
      |> Option.map ~f:P.toID
      |> Option.andThen ~f:(Analysis.getCurrentLiveValue m tl.id)
      (* don't filter on incomplete values *)
      |> Option.andThen ~f:(fun dv_ ->
             if dv_ = DIncomplete then None else Some dv_ )
  | None ->
      None


let isThreadMember (tl : toplevel) (ti : tokenInfo) =
  let id = tid ti.token in
  TL.asHandler tl
  |> Option.map ~f:(fun x -> x.ast)
  |> Option.andThen ~f:(AST.findParentOfWithin_ id)
  |> Option.map ~f:(fun e ->
         match e with F (_, Thread _) -> true | _ -> false )
  |> Option.withDefault ~default:false


let paramTipeForTarget (a : autocomplete) (tl : toplevel) (ti : tokenInfo) :
    tipe =
  let id = tid ti.token in
  TL.asHandler tl
  |> Option.map ~f:(fun x -> x.ast)
  |> Option.andThen ~f:(fun ast -> AST.getParamIndex ast id)
  |> Option.andThen ~f:(fun (name, index) ->
         a.functions
         |> List.find ~f:(fun f -> name = f.fnName)
         |> Option.map ~f:(fun x -> x.fnParameters)
         |> Option.andThen ~f:(List.getAt ~index)
         |> Option.map ~f:(fun x -> x.paramTipe) )
  |> Option.withDefault ~default:TAny


let matchesTypes
    (isThreadMemberVal : bool) (paramTipe : tipe) (dv : dval option) :
    function_ -> bool =
 fun fn ->
  let matchesReturnType = RT.isCompatible fn.fnReturnTipe paramTipe in
  let matchesParamType =
    match dv with
    | Some dval ->
        if isThreadMemberVal
        then None <> findCompatibleThreadParam fn (RT.typeOf dval)
        else None <> findParamByType fn (RT.typeOf dval)
    | None ->
        true
  in
  matchesReturnType && matchesParamType


(* ------------------------------------ *)
(* Dynamic Items *)
(* ------------------------------------ *)

let qLiteral (s : string) : autocompleteItem option =
  if Runtime.isStringLiteral s
  then
    if Runtime.isValidDisplayString s
    then Some (ACLiteral (Runtime.convertDisplayStringToLiteral s))
    else None
  else if Decoders.isLiteralRepr s
  then Some (ACLiteral s)
  else if String.length s > 0
  then
    if String.startsWith ~prefix:(String.toLower s) "false"
    then Some (ACLiteral "false")
    else if String.startsWith ~prefix:(String.toLower s) "true"
    then Some (ACLiteral "true")
    else if String.startsWith ~prefix:(String.toLower s) "null"
    then Some (ACLiteral "null")
    else None
  else None


(* ------------------------------------ *)
(* Validators *)
(* ------------------------------------ *)

(*
  urls
  From https://www.w3.org/Addressing/URL/5_URI_BNF.html
  path = void | xpalphas [ / path ]
  xalpha = alpha | digit | safe | extra | escape
  xalphas = xalpha [ xalphas ]
  xpalpha = xalpha | +
  xpalphas = xpalpha [ xpalpha ]
  alpha = [a-zA-Z]
  digit = [0-9]
  safe = $ | - | _ | @ | . | &
  extra = ! | * | <doublequote> | ' | ( | ) | ,
  reserved = = | ; | / | # | ? | : | space
  escape = % hex hex
*)
(* let urlPathSafeCharacters = "[-a-zA-Z0-9$_@.&!*\"'(),%/]" *)
(* let nonUrlPathSafeCharacters = "[^-a-zA-Z0-9$_@.&!*\"'(),%/]" *)
(* let urlPathValidator = "[-a-zA-Z0-9$_@.&!*\"'(),%/]+" *)

(* allow : for parameter names. TODO: do better job parsing here *)
let eventNameSafeCharacters = "[-a-zA-Z0-9$_@.&!*\"'(),%/:]"

let nonEventNameSafeCharacters = "[^-a-zA-Z0-9$_@.&!*\"'(),%/:]"

let httpNameValidator = "/[-a-zA-Z0-9$_@.&!*\"'(),%/:]*"

let eventNameValidator = "[-a-zA-Z0-9$_@.&!*\"'(),%/:]+"

let varnameValidator = "[a-z_][a-zA-Z0-9_]*"

let varnamePatternValidator = varnameValidator

let constructorPatternValidator = "[A-Z_][a-zA-Z0-9_]*"

let constructorNameValidator = "Just|Nothing|Ok|Error"

let dbColTypeValidator = "\\[?[A-Z]\\w+\\]?"

let dbColNameValidator = "\\w+"

let dbNameValidator = "[A-Z][a-zA-Z0-9_]*"

let eventModifierValidator = "[a-zA-Z_][\\sa-zA-Z0-9_]*"

let httpVerbValidator = "[A-Z]+"

let eventSpaceValidator = "[A-Z0-9_]+"

let fieldNameValidator = ".+"

let keynameValidator = ".+"

let fnNameValidator = "[a-z][a-zA-Z0-9_]*"

(* NB: disallowing inital-capitals also defends against having a collision
 * between a function param name and a db name *)
let paramNameValidator = "[a-z][a-zA-Z0-9_]*"

let typeNameValidator = dbNameValidator

let paramTypeValidator = "[A-Za-z0-9_]*"

let assertValid pattern value : string =
  if Regex.exactly ~re:pattern value
  then value
  else Debug.crash ("Failed validator: " ^ pattern ^ ", " ^ value)


let validateHttpNameValidVarnames (httpName : string) =
  let route_variables (route : string) : string list =
    route
    |> String.split ~on:"/"
    |> List.filter ~f:(fun x -> String.length x > 0)
    |> List.filter ~f:(fun x -> String.startsWith ~prefix:":" x)
    |> List.map ~f:(fun x -> String.dropLeft ~count:1 x)
  in
  if route_variables httpName
     |> List.all ~f:(fun v -> Regex.exactly ~re:varnameValidator v)
  then None
  else Some ("route variables must match /" ^ varnameValidator ^ "/")


(* ------------------------------------ *)
(* Omniactions *)
(* ------------------------------------ *)

let rec stripCharsFromFront (disallowed : string) (s : string) : string =
  match String.uncons s with
  | None ->
      s
  | Some (c, rest) ->
      let needle = String.fromChar c in
      if Regex.contains ~re:(Regex.regex disallowed) needle
      then stripCharsFromFront disallowed rest
      else s


let stripChars (disallowed : string) (s : string) : string =
  Regex.replace ~re:(Regex.regex disallowed) ~repl:"" s


let removeExtraSlashes (s : string) : string =
  let s = Regex.replace ~re:(Regex.regex "/+") ~repl:"/" s in
  let s =
    if s <> "/" && String.endsWith ~suffix:"/" s
    then String.dropRight ~count:1 s
    else s
  in
  s


let cleanEventName (s : string) : string =
  s |> stripChars nonEventNameSafeCharacters |> removeExtraSlashes


let cleanDBName (s : string) : string =
  s
  |> stripChars "[^a-zA-Z0-9_]"
  |> stripCharsFromFront "[^a-zA-Z]"
  |> String.capitalize


let qNewDB (s : string) : omniAction option =
  let name = cleanDBName s in
  if name = ""
  then Some (NewDB None)
  else
    let validName = assertValid dbNameValidator name in
    Some (NewDB (Some validName))


let qFunction (s : string) : omniAction =
  let name =
    s
    |> stripChars "[^a-zA-Z0-9_]"
    |> stripCharsFromFront "[^a-zA-Z]"
    |> String.uncapitalize
  in
  if name = ""
  then NewFunction None
  else NewFunction (Some (assertValid fnNameValidator name))


let qHandler (s : string) : omniAction =
  let name = s |> cleanEventName |> String.uncapitalize in
  if name = ""
  then NewHandler None
  else NewHandler (Some (assertValid eventNameValidator name))


let qHTTPHandler (s : string) : omniAction =
  let name = cleanEventName s in
  if name = ""
  then NewHTTPHandler None
  else if String.startsWith ~prefix:"/" name
  then NewHTTPHandler (Some (assertValid httpNameValidator name))
  else NewHTTPHandler (Some (assertValid httpNameValidator ("/" ^ name)))


let qEventSpace (s : string) : omniAction option =
  let name = s |> String.toUpper |> stripChars "[^A-Z0-9_]" in
  if name = ""
  then None
  else Some (NewEventSpace (assertValid eventSpaceValidator name))


let isDynamicItem (item : autocompleteItem) : bool =
  match item with
  | ACLiteral _ ->
      true
  | ACOmniAction (Goto _) ->
      false
  | ACOmniAction _ ->
      true
  | ACEventSpace _ ->
      false (* false because we want the static items to be first *)
  | ACEventName _ ->
      true
  | ACDBName _ ->
      true
  | _ ->
      false


let isStaticItem (item : autocompleteItem) : bool = not (isDynamicItem item)

let toDynamicItems
    (_space : handlerSpace option)
    (_targetTL : toplevel option)
    (_targetTI : tokenInfo option)
    (_q : string) : autocompleteItem list =
  match None with
  | None ->
      []
  (* | Some (_, PExpr _) -> *)
  (*     Option.values [qLiteral q] *)
  (* | Some (_, PField _) -> *)
  (*     [ACField q] *)
  (* | Some (_, PEventSpace _) -> *)
  (*     if q == "" then [] else [ACEventSpace (String.toUpper q)] *)
  (* | Some (_, PEventName _) -> *)
  (*     if q == "" *)
  (*     then if space == Some HSHTTP then [ACEventName "/"] else [] *)
  (*     else [ACEventName (cleanEventName q)] *)
  (* | Some (_, PDBName _) -> *)
  (*     if q == "" then [] else [ACDBName (cleanDBName q)] *)
  | _ ->
      []


let withDynamicItems
    (targetTL : toplevel option)
    (targetTI : tokenInfo option)
    (query : string)
    (acis : autocompleteItem list) : autocompleteItem list =
  let space = Option.andThen ~f:TL.spaceOf targetTL in
  let new_ = toDynamicItems space targetTL targetTI query in
  let withoutDynamic = List.filter ~f:isStaticItem acis in
  withoutDynamic @ new_


let fnGotoName (name : string) : string = "Just to function: " ^ name

let tlGotoName (tl : toplevel) : string =
  match tl.data with
  | TLHandler h ->
      "Jump to handler: "
      ^ (h.spec.module_ |> B.toMaybe |> Option.withDefault ~default:"Undefined")
      ^ "::"
      ^ (h.spec.name |> B.toMaybe |> Option.withDefault ~default:"Undefined")
      ^ " - "
      ^ ( h.spec.modifier
        |> B.toMaybe
        |> Option.withDefault ~default:"Undefined" )
  | TLDB db ->
      "Jump to DB: "
      ^ (db.dbName |> B.toMaybe |> Option.withDefault ~default:"Unnamed DB")
  | TLFunc _ ->
      Debug.crash "cannot happen"
  | TLTipe _ ->
      Debug.crash "cannot happen"


let tlDestinations (m : model) : autocompleteItem list =
  let tls =
    m.toplevels
    |> List.sortBy ~f:tlGotoName
    |> List.map ~f:(fun tl -> Goto (TL.asPage tl, tl.id, tlGotoName tl))
  in
  let ufs =
    List.filterMap
      ~f:(fun fn ->
        match fn.ufMetadata.ufmName with
        | Partial _ | Blank _ ->
            None
        | F (_, name) ->
            Some (Goto (FocusedFn fn.ufTLID, fn.ufTLID, fnGotoName name)) )
      m.userFunctions
  in
  List.map ~f:(fun x -> ACOmniAction x) (tls @ ufs)


let matcher (a : autocomplete) (item : autocompleteItem) =
  match item with
  | ACFunction fn ->
      let isThreadMemberVal, paramTipe =
        match (a.targetTL, a.targetTI) with
        | Some tl, Some ti ->
            (isThreadMember tl ti, paramTipeForTarget a tl ti)
        | _ ->
            (false, TAny)
      in
      matchesTypes isThreadMemberVal paramTipe a.targetDval fn
  | _ ->
      true


(* ------------------------------------ *)
(* Create the list *)
(* ------------------------------------ *)
let generate (m : model) (a : autocomplete) : autocomplete =
  let _space = a.targetTL |> Option.map ~f:TL.spaceOf in
  let varnames, dval =
    match (a.targetTL, a.targetTI) with
    | Some tl, Some ti ->
        let id = tid ti.token in
        (Analysis.getCurrentAvailableVarnames m tl id, dvalForTarget m tl ti)
    | _ ->
        ([], None)
  in
  let fields =
    []
    (*     match dval with *)
    (* | Some dv when RT.typeOf dv = TObj -> *)
    (*   ( match a.target with *)
    (*   | Some (_, pd) when P.typeOf pd = Field -> *)
    (*       List.map ~f:(fun x -> ACField x) (dvalFields dv) *)
    (*   | _ -> *)
    (*       [] ) *)
    (* | _ -> *)
    (* [] *)
  in
  (* let isExpression = *)
  (*   match a.target with Some (_, p) -> P.typeOf p = Expr | None -> false *)
  (* in *)
  (* functions *)
  let funcList = a.functions in
  (* let funcList = if isExpression then a.functions else [] in *)
  let functions = List.map ~f:(fun x -> ACFunction x) funcList in
  let constructors =
    [ ACConstructorName "Just"
    ; ACConstructorName "Nothing"
    ; ACConstructorName "Ok"
    ; ACConstructorName "Error" ]
  in
  let extras =
    []
    (* match a.target with *)
    (* | Some (_, p) -> *)
    (*   ( match P.typeOf p with *)
    (*   (* autocomplete HTTP verbs if the handler is in the HTTP event space *) *)
    (*   | EventModifier -> *)
    (*     ( match space with *)
    (*     | Some HSHTTP -> *)
    (*         [ ACHTTPModifier "GET" *)
    (*         ; ACHTTPModifier "POST" *)
    (*         ; ACHTTPModifier "PUT" *)
    (*         ; ACHTTPModifier "DELETE" *)
    (*         ; ACHTTPModifier "PATCH" ] *)
    (*     | Some HSCron -> *)
    (*         [ ACCronTiming "Daily" *)
    (*         ; ACCronTiming "Weekly" *)
    (*         ; ACCronTiming "Fortnightly" *)
    (*         ; ACCronTiming "Every 1hr" *)
    (*         ; ACCronTiming "Every 12hrs" *)
    (*         ; ACCronTiming "Every 1min" ] *)
    (*     | Some HSOther -> *)
    (*         [] *)
    (*     | Some HSEmpty -> *)
    (*         [] *)
    (*     | None -> *)
    (*         [] ) *)
    (*   | EventSpace -> *)
    (*       [ACEventSpace "HTTP"; ACEventSpace "CRON"] *)
    (*   | DBColType -> *)
    (*       let builtins = *)
    (*         ["String"; "Int"; "Boolean"; "Float"; "Password"; "Date"; "UUID"] *)
    (*       in *)
    (*       let compound = List.map ~f:(fun s -> "[" ^ s ^ "]") builtins in *)
    (*       List.map ~f:(fun x -> ACDBColType x) (builtins @ compound) *)
    (*   | ParamTipe -> *)
    (*       let userTypes = *)
    (*         m.userTipes *)
    (*         |> List.filterMap ~f:UserTypes.toTUserType *)
    (*         |> List.map ~f:(fun t -> ACParamTipe t) *)
    (*       in *)
    (*       [ ACParamTipe TAny *)
    (*       ; ACParamTipe TStr *)
    (*       ; ACParamTipe TInt *)
    (*       ; ACParamTipe TBool *)
    (*       ; ACParamTipe TFloat *)
    (*       ; ACParamTipe TDate *)
    (*       ; ACParamTipe TObj *)
    (*       ; ACParamTipe TBlock *)
    (*       ; ACParamTipe TPassword *)
    (*       ; ACParamTipe TUuid *)
    (*       ; ACParamTipe TList ] *)
    (*       @ userTypes *)
    (*   | TypeFieldTipe -> *)
    (*       [ ACTypeFieldTipe TStr *)
    (*       ; ACTypeFieldTipe TInt *)
    (*       ; ACTypeFieldTipe TBool *)
    (*       ; ACTypeFieldTipe TFloat *)
    (*       ; ACTypeFieldTipe TDate *)
    (*       ; ACTypeFieldTipe TPassword *)
    (*       ; ACTypeFieldTipe TUuid ] *)
    (*   | Pattern -> *)
    (*     ( match dval with *)
    (*     | Some dv when RT.typeOf dv = TResult -> *)
    (*         [ACConstructorName "Ok"; ACConstructorName "Error"] *)
    (*     | Some dv when RT.typeOf dv = TOption -> *)
    (*         [ACConstructorName "Just"; ACConstructorName "Nothing"] *)
    (*     | _ -> *)
    (*         constructors ) *)
    (*   | _ -> *)
    (*       [] ) *)
    (* | _ -> *)
    (*     [] *)
  in
  let exprs =
    (* if isExpression *)
    (* then *)
    let varnames = List.map ~f:(fun x -> ACVariable x) varnames in
    let keywords =
      List.map ~f:(fun x -> ACKeyword x) [KLet; KIf; KLambda; KMatch]
    in
    varnames @ constructors @ keywords @ functions
    (* else [] *)
  in
  let items = extras @ exprs @ fields in
  {a with allCompletions = items; targetDval = dval}


let filter
    (_m : model)
    (a : autocomplete)
    (list : autocompleteItem list)
    (query : string) : autocompleteItem list * autocompleteItem list =
  let lcq = query |> String.toLower in
  let stringify i =
    (if 1 >= String.length lcq then asName i else asString i)
    |> Regex.replace ~re:(Regex.regex {js|⟶|js}) ~repl:"->"
  in
  (* HACK: dont show Gotos when the query is "" *)
  let list =
    List.filter list ~f:(function
        | ACOmniAction (Goto _) ->
            query <> ""
        | _ ->
            true )
  in
  (* split into different lists *)
  let dynamic, candidates0 = List.partition ~f:isDynamicItem list in
  let candidates1, notSubstring =
    List.partition
      ~f:(stringify >> String.toLower >> String.contains ~substring:lcq)
      candidates0
  in
  let startsWith, candidates2 =
    List.partition
      ~f:(stringify >> String.startsWith ~prefix:query)
      candidates1
  in
  let startsWithCI, candidates3 =
    List.partition
      ~f:(stringify >> String.toLower >> String.startsWith ~prefix:lcq)
      candidates2
  in
  let substring, substringCI =
    List.partition
      ~f:(stringify >> String.contains ~substring:query)
      candidates3
  in
  let stringMatch, _notMatched =
    List.partition
      ~f:(asName >> String.toLower >> containsOrdered lcq)
      notSubstring
  in
  let allMatches =
    [dynamic; startsWith; startsWithCI; substring; substringCI; stringMatch]
    |> List.concat
  in
  (* Now split list by type validity *)
  List.partition ~f:(matcher a) allMatches


let refilter (m : model) (query : string) (old : autocomplete) : autocomplete =
  (* add or replace the literal the user is typing to the completions *)
  let fudgedCompletions =
    if old.isCommandMode
    then List.filter ~f:isStaticItem old.allCompletions
    else withDynamicItems old.targetTL old.targetTI query old.allCompletions
  in
  let newCompletions, invalidCompletions =
    filter m old fudgedCompletions query
  in
  let oldHighlight = highlighted old in
  let allCompletions = newCompletions @ invalidCompletions in
  let newCount = List.length allCompletions in
  let oldHighlightNewPos =
    oldHighlight
    |> Option.andThen ~f:(fun oh -> List.elemIndex ~value:oh allCompletions)
  in
  let index =
    (* Clear the highlight conditions *)
    if query = ""
       (* when we had previously highlighted something due to any actual match *)
       && ( (old.index <> None && false)
          (* TODO: this condition was important old.value <> query *)
          (* or this condition previously held and nothing has changed *)
          || old.index = None )
    then None
    else
      (* If an entry is highlighted, and you press another *)
      (* valid key for that entry, keep it highlighted *)
      match oldHighlightNewPos with
      | Some i ->
          Some i
      (* If an entry vanishes, highlight 0 *)
      | None ->
          (* if nothing matches, highlight nothing *)
          if newCount = 0
          then
            None
            (* we matched something but its gone, go to top of *)
            (* list *)
          else Some 0
  in
  {old with index; completions = newCompletions; invalidCompletions}


let regenerate (m : model) (str : string) (a : autocomplete) : autocomplete =
  generate m a |> refilter m str


(* ---------------------------- *)
(* Autocomplete state *)
(* ---------------------------- *)
let reset (m : model) : autocomplete =
  let userFunctionMetadata =
    m.userFunctions
    |> List.map ~f:(fun x -> x.ufMetadata)
    |> List.filterMap ~f:Functions.ufmToF
  in
  let functions =
    m.builtInFunctions
    |> List.filter ~f:(fun f ->
           (not f.fnDeprecated) || Refactor.usedFn m f.fnName )
  in
  let functions = functions @ userFunctionMetadata in
  {Defaults.defaultModel.fluidState.ac with functions} |> regenerate m ""


let init m = reset m

let numCompletions (a : autocomplete) : int =
  List.length a.completions + List.length a.invalidCompletions


let selectDown (a : autocomplete) : autocomplete =
  match a.index with
  | Some index ->
      let max_ = numCompletions a in
      let max = max max_ 1 in
      let new_ = (index + 1) mod max in
      {a with index = Some new_}
  | None ->
      a


let selectUp (a : autocomplete) : autocomplete =
  match a.index with
  | Some index ->
      let max = numCompletions a - 1 in
      {a with index = Some (if index <= 0 then max else index - 1)}
  | None ->
      a


let documentationForItem (aci : autocompleteItem) : string option =
  match aci with
  | ACFunction f ->
      let desc =
        if String.length f.fnDescription <> 0
        then f.fnDescription
        else "Function call with no description"
      in
      let desc = if f.fnDeprecated then "DEPRECATED: " ^ desc else desc in
      Some desc
  | ACCommand c ->
      Some (c.doc ^ " (" ^ c.shortcut ^ ")")
  | ACConstructorName "Just" ->
      Some "An Option containing a value"
  | ACConstructorName "Nothing" ->
      Some "An Option representing Nothing"
  | ACConstructorName "Ok" ->
      Some "A successful Result containing a value"
  | ACConstructorName "Error" ->
      Some "A Result representing a failure"
  | ACConstructorName name ->
      Some ("TODO: this should never occur: the constructor " ^ name)
  | ACField fieldname ->
      Some ("The '" ^ fieldname ^ "' field of the object")
  | ACVariable var ->
      if String.isCapitalized var
      then Some ("The database '" ^ var ^ "'")
      else Some ("The variable '" ^ var ^ "'")
  | ACLiteral lit ->
      Some ("The literal value '" ^ lit ^ "'")
  | ACKeyword KLet ->
      Some "A `let` expression allows you assign a variable to an expression"
  | ACKeyword KIf ->
      Some "An `if` expression allows you to branch on a boolean condition"
  | ACKeyword KLambda ->
      Some
        "A `lambda` creates an anonymous function. This is most often used for iterating through lists"
  | ACKeyword KMatch ->
      Some
        "A `match` expression allows you to pattern match on a value, and return different expressions based on many possible conditions"
  | ACOmniAction _ ->
      None
  | ACHTTPModifier verb ->
      Some ("Make this handler match the " ^ verb ^ " HTTP verb")
  | ACCronTiming timing ->
      Some ("Request this handler to trigger " ^ timing)
  | ACEventSpace "HTTP" ->
      Some "This handler will respond to HTTP requests"
  | ACEventSpace "CRON" ->
      Some "This handler will periodically trigger"
  | ACEventSpace name ->
      Some ("This handler will respond when events are emitted to " ^ name)
  | ACEventName name ->
      Some ("Respond to events or HTTP requests named " ^ name)
  | ACDBName name ->
      Some ("Set the DB's name to " ^ name)
  | ACDBColType tipe ->
      Some ("This field will be a " ^ tipe)
  | ACParamTipe tipe ->
      Some ("This parameter will be a " ^ RT.tipe2str tipe)
  | ACTypeFieldTipe tipe ->
      Some ("This parameter will be a " ^ RT.tipe2str tipe)
  | ACExtra _ ->
      None


let setTargetTL (m : model) (tl : toplevel option) (a : autocomplete) :
    autocomplete =
  {a with targetTL = tl} |> regenerate m a.query


let setTargetTI (m : model) (ti : tokenInfo option) (a : autocomplete) :
    autocomplete =
  {a with targetTI = ti} |> regenerate m a.query


(* ------------------------------------ *)
(* Commands *)
(* ------------------------------------ *)
let enableCommandMode (a : autocomplete) : autocomplete =
  {a with isCommandMode = true}
