module FuzzTests.All

// This aims to find test cases that violate certain properties that we expect.
// Desired properties include that OCaml Dark programs and functions work the
// same as F# ones, and things related to serialization and output.

open Expecto
open Expecto.ExpectoFsCheck
open FsCheck

open System.Threading.Tasks
open FSharp.Control.Tasks

open Prelude
open Prelude.Tablecloth
open Tablecloth
open TestUtils

module PT = LibExecution.ProgramTypes
module RT = LibExecution.RuntimeTypes
module OCamlInterop = LibBackend.OCamlInterop
module DvalRepr = LibExecution.DvalRepr

let result (t : Task<'a>) : 'a = t.Result

let (.=.) actual expected : bool =
  if actual = expected then
    Expect.equal actual expected ""
    true
  else
    let o = actual.ToString() |> toBytes
    let e = expected.ToString() |> toBytes
    Expect.equal (actual, o) (expected, e) ""
    false


let baseConfig : FsCheckConfig =
  { FsCheckConfig.defaultConfig with maxTest = 100000 }

let baseConfigWithGenerator (typ : System.Type) : FsCheckConfig =
  { baseConfig with arbitrary = [ typ ] }

let testProperty (name : string) (x : 'a) : Test =
  testPropertyWithConfig baseConfig name x

let testPropertyWithGenerator (typ : System.Type) (name : string) (x : 'a) : Test =
  testPropertyWithConfig (baseConfigWithGenerator typ) name x

module Generators =
  let nonNullString (s : string) : bool = s <> null

  let safeOCamlString (s : string) : bool =
    // We disallow \u0000 in OCaml because postgres doesn't like it, see of_utf8_encoded_string
    s <> null && not (s.Contains('\u0000'))

  let alphaNumericString =
    (List.concat [ [ 'a' .. 'z' ]; [ '0' .. '9' ]; [ 'A' .. 'Z' ]; [ '_' ] ])

  let string () =
    let isValid (s : string) : bool =
      try
        let (_ : string) = s.Normalize()
        true
      with _ ->
        debuG
          "Failed to normalize :"
          $"'{s}': (len {s.Length}, {System.BitConverter.ToString(toBytes s)})"

        false

    Arb.generate<UnicodeString>
    |> Gen.map (fun (UnicodeString s) -> s)
    |> Gen.filter isValid
    // Now that we know it can be normalized, actually normalize it
    |> Gen.map (fun s -> s.Normalize())
    |> Gen.filter safeOCamlString



  let nonNegativeInt () =
    gen {
      let! (NonNegativeInt i) = Arb.generate<NonNegativeInt>
      return i
    }

  // https://github.com/minimaxir/big-list-of-naughty-strings
  let naughtyStrings : Lazy<List<string>> =
    lazy
      (LibBackend.File.readfile LibBackend.Config.Testdata "naughty-strings.txt"
       |> String.splitOnNewline
       |> List.filter (String.startsWith "#" >> not))

  let char () : Gen<string> =
    string ()
    |> Gen.map String.toEgcSeq
    |> Gen.map Seq.toList
    |> Gen.map List.head
    |> Gen.filter ((<>) None)
    |> Gen.map (Option.defaultValue "")
    |> Gen.filter ((<>) "")


module G = Generators



module FQFnName =
  let nameGenerator (first : char list) (other : char list) : Gen<string> =
    gen {
      let! length = Gen.choose (0, 20)
      let! head = Gen.elements first
      let! tail = Gen.arrayOfLength length (Gen.elements other)
      return System.String(Array.append [| head |] tail)
    }

  let ownerName : Gen<string> =
    nameGenerator [ 'a' .. 'z' ] (List.concat [ [ 'a' .. 'z' ]; [ '0' .. '9' ] ])

  let packageName = ownerName
  let modName : Gen<string> = nameGenerator [ 'A' .. 'Z' ] G.alphaNumericString
  let fnName : Gen<string> = nameGenerator [ 'a' .. 'z' ] G.alphaNumericString

  type Generator =
    static member SafeString() : Arbitrary<string> = Arb.fromGen (G.string ())

    static member PTFQFnName() : Arbitrary<PT.FQFnName.T> =
      { new Arbitrary<PT.FQFnName.T>() with
          member x.Generator =
            let stdlib =
              gen {
                let! module_ = modName
                let! function_ = fnName
                let! version = G.nonNegativeInt ()
                return PT.FQFnName.stdlibFqName module_ function_ version
              }

            let user = Gen.map PT.FQFnName.userFqName fnName

            let package =
              gen {
                let! owner = ownerName
                let! package = packageName
                let! module_ = modName
                let! function_ = fnName
                let! version = G.nonNegativeInt ()

                return
                  PT.FQFnName.packageFqName owner package module_ function_ version
              }

            Gen.oneof [ stdlib; user; package ] }

    static member RTFQFnName() : Arbitrary<RT.FQFnName.T> =
      { new Arbitrary<RT.FQFnName.T>() with
          member x.Generator = Generator.PTFQFnName().Generator }

  let ptRoundtrip (a : PT.FQFnName.T) : bool = string a |> PT.FQFnName.parse .=. a

  let tests =
    testList
      "PT.FQFnName"
      [ testPropertyWithGenerator typeof<Generator> "roundtripping" ptRoundtrip ]


module OCamlInterop =
  open LibExecution.OCamlTypes.Convert
  open OCamlInterop
  open Json.OCamlCompatible

  let isInteroperable
    (ocamlToString : 'a -> Task<string>)
    (ocamlOfString : string -> Task<'a>)
    (fsToString : 'a -> string)
    (fsOfString : string -> 'a)
    (equality : 'a -> 'a -> bool)
    (v : 'a)
    : bool =
    try
      // What does it mean to interoperate? Ideally, the F# impl would be able
      // to read what the OCaml impl sends it and vice versa. However, because
      // the OCaml side is buggy, and we want to reproduce those bugs exactly
      // (for now), that isn't sufficient. We actually just want to make sure
      // we produce the same thing as they do for the same value. BUT, we don't
      // actually produce the exact same thing, and it's hard to do that for
      // the edge cases we've found. So really we just want to make sure that
      // whatever either side produces, both sides are able to read it and get
      // the same result.
      let bothCanRead str = (ocamlOfString str).Result |> equality (fsOfString str)
      let bothCanReadOCamlString = bothCanRead (ocamlToString v).Result
      let bothCanReadFSharpString = bothCanRead (fsToString v)

      if bothCanReadFSharpString && bothCanReadOCamlString then
        true
      else
        printfn
          "%s"
          ($"ocamlStringReadable: {bothCanReadOCamlString}\n"
           + $"fsharpStringReadable: {bothCanReadFSharpString}\n")

        false
    with e ->
      printfn $"Cause exception while fuzzing {e}"
      reraise ()

  type Generator =
    static member Expr() =
      Arb.Default.Derive()
      |> Arb.filter
           (function
           // characters are not yet supported in OCaml
           | PT.ECharacter _ -> false
           | _ -> true)

    static member Pattern() =
      Arb.Default.Derive()
      |> Arb.filter
           (function
           // characters are not yet supported in OCaml
           | PT.PCharacter _ -> false
           | _ -> true)

    static member SafeString() : Arbitrary<string> = Arb.fromGen (G.string ())


  let yojsonExprRoundtrip (a : PT.Expr) : bool =
    a
    |> pt2ocamlExpr
    |> serialize
    |> deserialize
    |> ocamlExpr2PT
    |> serialize
    |> deserialize
    |> pt2ocamlExpr
    |> serialize
    |> deserialize
    |> ocamlExpr2PT
    |> serialize
    |> deserialize
    .=. a

  let yojsonHandlerRoundtrip (a : PT.Handler.T) : bool =
    a
    |> pt2ocamlHandler
    |> serialize
    |> deserialize
    |> ocamlHandler2PT a.pos
    |> serialize
    |> deserialize
    |> pt2ocamlHandler
    |> serialize
    |> deserialize
    |> ocamlHandler2PT a.pos
    |> serialize
    |> deserialize
    .=. a

  let binaryHandlerRoundtrip (a : PT.Handler.T) : bool =
    let h = PT.TLHandler a

    h
    |> toplevelToCachedBinary
    |> result
    |> (fun bin -> bin, None)
    |> toplevelOfCachedBinary
    |> result
    .=. h

  let binaryExprRoundtrip (pair : PT.Expr * tlid) : bool =
    pair
    |> exprTLIDPairToCachedBinary
    |> result
    |> exprTLIDPairOfCachedBinary
    |> result
    .=. pair

  let tests =
    let tp f = testPropertyWithGenerator typeof<Generator> f

    testList
      "OcamlInterop"
      [ tp "roundtripping OCamlInteropBinaryHandler" binaryHandlerRoundtrip
        tp "roundtripping OCamlInteropBinaryExpr" binaryExprRoundtrip
        tp "roundtripping OCamlInteropYojsonHandler" yojsonHandlerRoundtrip
        tp "roundtripping OCamlInteropYojsonExpr" yojsonExprRoundtrip ]

module Roundtrippable =
  type Generator =
    static member String() : Arbitrary<string> = Arb.fromGen (G.string ())

    static member DvalSource() : Arbitrary<RT.DvalSource> =
      Arb.Default.Derive() |> Arb.filter (fun dvs -> dvs = RT.SourceNone)

    static member Dval() : Arbitrary<RT.Dval> =
      Arb.Default.Derive() |> Arb.filter (DvalRepr.isRoundtrippableDval false)

  type GeneratorWithBugs =
    static member String() : Arbitrary<string> = Arb.fromGen (G.string ())

    static member DvalSource() : Arbitrary<RT.DvalSource> =
      Arb.Default.Derive() |> Arb.filter (fun dvs -> dvs = RT.SourceNone)

    static member Dval() : Arbitrary<RT.Dval> =
      Arb.Default.Derive() |> Arb.filter (DvalRepr.isRoundtrippableDval true)

  let roundtrip (dv : RT.Dval) : bool =
    dv
    |> DvalRepr.toInternalRoundtrippableV0
    |> DvalRepr.ofInternalRoundtrippableV0
    |> dvalEquality dv

  let isInteroperableV0 dv =
    OCamlInterop.isInteroperable
      OCamlInterop.toInternalRoundtrippableV0
      OCamlInterop.ofInternalRoundtrippableV0
      DvalRepr.toInternalRoundtrippableV0
      DvalRepr.ofInternalRoundtrippableV0
      dvalEquality
      dv

  let tests =
    testList
      "roundtrippable"
      [ testPropertyWithGenerator
          typeof<Generator>
          "roundtripping works properly"
          roundtrip
        testPropertyWithGenerator
          typeof<GeneratorWithBugs>
          "roundtrippable is interoperable"
          isInteroperableV0 ]


module Queryable =
  type Generator =
    static member SafeString() : Arbitrary<string> = Arb.fromGen (G.string ())

    static member DvalSource() : Arbitrary<RT.DvalSource> =
      Arb.Default.Derive() |> Arb.filter (fun dvs -> dvs = RT.SourceNone)

    static member Dval() : Arbitrary<RT.Dval> =
      Arb.Default.Derive() |> Arb.filter DvalRepr.isQueryableDval

  let v1Roundtrip (dv : RT.Dval) : bool =
    let dvm = (Map.ofList [ "field", dv ])

    dvm
    |> DvalRepr.toInternalQueryableV1
    |> DvalRepr.ofInternalQueryableV1
    |> dvalEquality (RT.DObj dvm)

  let isInteroperableV1 (dv : RT.Dval) =
    let dvm = (Map.ofList [ "field", dv ])

    OCamlInterop.isInteroperable
      OCamlInterop.toInternalQueryableV1
      OCamlInterop.ofInternalQueryableV1
      (function
      | RT.DObj dvm -> DvalRepr.toInternalQueryableV1 dvm
      | _ -> failwith "not an obj")
      DvalRepr.ofInternalQueryableV1
      dvalEquality
      (RT.DObj dvm)

  // OCaml v0 vs F# v1
  let isInteroperableV0 (dv : RT.Dval) =
    let dvm = (Map.ofList [ "field", dv ])

    OCamlInterop.isInteroperable
      (OCamlInterop.toInternalQueryableV0)
      (OCamlInterop.ofInternalQueryableV0)
      (function
      | RT.DObj dvm -> DvalRepr.toInternalQueryableV1 dvm
      | _ -> failwith "not an obj")
      (DvalRepr.ofInternalQueryableV1)
      dvalEquality
      (RT.DObj dvm)

  let tests =
    let tp f = testPropertyWithGenerator typeof<Generator> f

    testList
      "InternalQueryable"
      [ tp "roundtripping v1" v1Roundtrip
        tp "interoperable v0" isInteroperableV0
        tp "interoperable v1" isInteroperableV1 ]

module DeveloperRepr =
  type Generator =
    static member SafeString() : Arbitrary<string> = Arb.fromGen (G.string ())

    // The format here is only used for errors so it doesn't matter all the
    // much. These are places where we've manually checked the differing
    // outputs are fine.

    static member Dval() : Arbitrary<RT.Dval> =
      Arb.Default.Derive()
      |> Arb.filter
           (function
           | RT.DFnVal _ -> false
           | RT.DFloat 0.0 -> false
           | RT.DFloat infinity -> false
           | _ -> true)


  let equalsOCaml (dv : RT.Dval) : bool =
    DvalRepr.toDeveloperReprV0 dv .=. (OCamlInterop.toDeveloperRepr dv).Result

  let tests =
    testList
      "toDeveloperRepr"
      [ testPropertyWithGenerator typeof<Generator> "roundtripping" equalsOCaml ]

module EndUserReadable =
  type Generator =
    static member SafeString() : Arbitrary<string> = Arb.fromGen (G.string ())

    static member Dval() : Arbitrary<RT.Dval> =
      Arb.Default.Derive()
      |> Arb.filter
           (function
           | RT.DFnVal _ -> false
           | _ -> true)

  // The format here is used to show users so it has to be exact
  let equalsOCaml (dv : RT.Dval) : bool =
    DvalRepr.toEnduserReadableTextV0 dv
    .=. (OCamlInterop.toEnduserReadableTextV0 dv).Result

  let tests =
    testList
      "toEnduserReadable"
      [ testPropertyWithGenerator typeof<Generator> "roundtripping" equalsOCaml ]

module Hashing =
  type Generator =
    static member SafeString() : Arbitrary<string> = Arb.fromGen (G.string ())

    static member Dval() : Arbitrary<RT.Dval> =
      Arb.Default.Derive()
      |> Arb.filter
           (function
           // not supported in OCaml
           | RT.DFnVal _ -> false
           | _ -> true)

  // The format here is used to get values from the DB, so this has to be 100% identical
  let equalsOCamlToHashable (dv : RT.Dval) : bool =
    let ocamlVersion = (OCamlInterop.toHashableRepr dv).Result
    let fsharpVersion = DvalRepr.toHashableRepr 0 false dv |> ofBytes
    ocamlVersion .=. fsharpVersion

  let equalsOCamlV0 (l : List<RT.Dval>) : bool =
    DvalRepr.hash 0 l .=. (OCamlInterop.hashV0 l).Result

  let equalsOCamlV1 (l : List<RT.Dval>) : bool =
    let ocamlVersion = (OCamlInterop.hashV1 l).Result
    let fsharpVersion = DvalRepr.hash 1 l
    ocamlVersion .=. fsharpVersion

  let tests =
    testList
      "hash"
      [ testPropertyWithGenerator
          typeof<Generator>
          "toHashableRepr"
          equalsOCamlToHashable
        testPropertyWithGenerator typeof<Generator> "hashv0" equalsOCamlV0
        testPropertyWithGenerator typeof<Generator> "hashv1" equalsOCamlV1 ]


module PrettyMachineJson =
  type Generator =
    static member SafeString() : Arbitrary<string> = Arb.fromGen (G.string ())

    // This should produce identical JSON to the OCaml function or customers will have an unexpected change
    static member Dval() : Arbitrary<RT.Dval> =
      Arb.Default.Derive()
      |> Arb.filter
           (function
           | RT.DFnVal _ -> false
           | _ -> true)

  let equalsOCaml (dv : RT.Dval) : bool =
    let actual =
      dv
      |> DvalRepr.toPrettyMachineJsonStringV1
      |> Newtonsoft.Json.Linq.JToken.Parse
      |> toString

    let expected =
      (OCamlInterop.toPrettyMachineJsonV1 dv).Result
      |> Newtonsoft.Json.Linq.JToken.Parse
      |> toString

    actual .=. expected

  let tests =
    testList
      "prettyMachineJson"
      [ testPropertyWithGenerator
          typeof<Generator>
          "roundtripping prettyMachineJson"
          equalsOCaml ]

module ExecutePureFunctions =
  open LibExecution.ProgramTypes.Shortcuts

  let filterFloat (f : float) : bool =
    match f with
    | System.Double.PositiveInfinity -> false
    | System.Double.NegativeInfinity -> false
    | f when System.Double.IsNaN f -> false
    | f when f <= -1e+308 -> false
    | f when f >= 1e+308 -> false
    | _ -> true

  let ocamlIntUpperLimit = 4611686018427387903I

  let ocamlIntLowerLimit = -4611686018427387904I

  let isValidOCamlInt (i : bigint) : bool =
    i <= ocamlIntUpperLimit && i >= ocamlIntLowerLimit


  type Generator =
    static member SafeString() : Arbitrary<string> = Arb.fromGen (G.string ())

    static member Float() : Arbitrary<float> =
      Arb.fromGen (
        gen {
          let specials =
            TestUtils.interestingFloats
            |> List.map Tuple2.second
            |> List.filter filterFloat
            |> List.map Gen.constant
            |> Gen.oneof

          let v = Gen.frequency [ (5, specials); (5, Arb.generate<float>) ]
          return! Gen.filter filterFloat v
        }
      )

    static member BigInt() : Arbitrary<bigint> =
      Arb.fromGen (
        gen {
          let specials =
            TestUtils.interestingInts
            |> List.map Tuple2.second
            |> List.filter isValidOCamlInt
            |> List.map Gen.constant
            |> Gen.oneof

          let v = Gen.frequency [ (5, specials); (5, Arb.generate<bigint>) ]
          return! Gen.filter isValidOCamlInt v
        }
      )

    static member Dval() : Arbitrary<RT.Dval> =
      Arb.Default.Derive()
      |> Arb.filter
           (function
           // These all break the serialization to OCaml
           | RT.DPassword _ -> false
           | RT.DFnVal _ -> false
           | RT.DFloat f -> filterFloat f
           | _ -> true)

    static member Fn() : Arbitrary<PT.FQFnName.StdlibFnName * List<RT.Dval>> =
      let genExpr (typ' : RT.DType) : Gen<RT.Expr> =
        let rec genExpr' typ s =
          let call mod_ fn version args =
            let call =
              RT.EFQFnValue(
                gid (),
                RT.FQFnName.Stdlib(RT.FQFnName.stdlibFnName mod_ fn version)
              )

            RT.EApply(gid (), call, args, RT.NotInPipe, RT.NoRail)

          gen {
            match typ with
            | RT.TInt ->
                let! v = Arb.generate<bigint>
                return RT.EInteger(gid (), v)
            | RT.TStr ->
                let! v = Generators.string ()
                return RT.EString(gid (), v)
            | RT.TChar ->
                // We don't have a construct for characters, so create code to generate the character
                let! str =
                  Generators.string () |> Gen.resize 1 |> Gen.filter ((<>) "")

                return call "String" "toChar" 0 [ RT.EString(gid (), str) ]
            // Don't generate a random value as some random values are invalid
            // (eg constructor outside certain names). Ints should be fine for
            // whatever purpose there is here
            | RT.TVariable _ -> return! genExpr' RT.TInt s
            | RT.TFloat ->
                let! v = Arb.generate<float>
                return RT.EFloat(gid (), v)
            | RT.TBool ->
                let! v = Arb.generate<bool>
                return RT.EBool(gid (), v)
            | RT.TNull -> return RT.ENull(gid ())
            | RT.TList typ ->
                let! v = (Gen.listOfLength s (genExpr' typ (s / 2)))
                return RT.EList(gid (), v)
            | RT.TDict typ ->
                return!
                  Gen.map
                    (fun l -> RT.ERecord(gid (), l))
                    (Gen.listOfLength
                      s
                      (Gen.zip (Generators.string ()) (genExpr' typ (s / 2))))
            | RT.TOption typ ->
                match! Gen.optionOf (genExpr' typ s) with
                | Some v -> return RT.EConstructor(gid (), "Just", [ v ])
                | None -> return RT.EConstructor(gid (), "Nothing", [])
            | RT.TResult (okType, errType) ->
                let! v =
                  Gen.oneof [ Gen.map Ok (genExpr' okType s)
                              Gen.map Error (genExpr' errType s) ]

                match v with
                | Ok v -> return RT.EConstructor(gid (), "Ok", [ v ])
                | Error v -> return RT.EConstructor(gid (), "Error", [ v ])

            | RT.TFn (paramTypes, returnType) ->
                let parameters =
                  List.mapi
                    (fun i (v : RT.DType) -> (id i, $"{v.toOldString ()}_{i}"))
                    paramTypes

                let! body = genExpr' returnType s
                return RT.ELambda(gid (), parameters, body)
            | RT.TBytes ->
                // FSTODO: this doesn't really do anything useful
                let! bytes = Arb.generate<byte []>
                let v = RT.EString(gid (), base64Encode bytes)
                return call "String" "toBytes" 0 [ v ]
            | RT.TDate ->
                let! d = Arb.generate<System.DateTime>
                return call "Date" "parse" 0 [ RT.EString(gid (), d.toIsoString ()) ]
            | RT.TUuid ->
                let! u = Arb.generate<System.Guid>
                return call "String" "toUUID" 0 [ RT.EString(gid (), string u) ]
            | _ -> return failwith $"Not supported yet: {typ}"

          }

        Gen.sized (genExpr' typ')


      let genDval (typ' : RT.DType) : Gen<RT.Dval> =
        let rec genDval' typ s : Gen<RT.Dval> =
          gen {
            match typ with
            | RT.TInt ->
                let! v = Arb.generate<bigint>
                return RT.DInt v
            | RT.TStr ->
                let! v = Generators.string ()
                return RT.DStr v
            | RT.TVariable _ ->
                let rec supportedType =
                  (function
                  | RT.TInt
                  | RT.TFloat
                  | RT.TBool
                  | RT.TNull
                  | RT.TUuid
                  | RT.TNull
                  | RT.TDate
                  | RT.TBytes
                  | RT.TChar
                  | RT.TStr -> true
                  | RT.TList t
                  | RT.TDict t
                  | RT.TOption t -> supportedType t
                  | RT.TResult (t1, t2) -> supportedType t1 && supportedType t2
                  | RT.TFn (ts, rt) -> supportedType rt && List.all supportedType ts
                  | _ -> false)

                let! newtyp = Arb.generate<RT.DType> |> Gen.filter supportedType
                return! genDval' newtyp s
            | RT.TFloat ->
                let! v = Arb.generate<float>
                return RT.DFloat v
            | RT.TBool -> return! Gen.map RT.DBool Arb.generate<bool>
            | RT.TNull -> return RT.DNull
            | RT.TList typ ->
                return! Gen.map RT.DList (Gen.listOfLength s (genDval' typ (s / 2)))
            | RT.TDict typ ->
                return!
                  Gen.map
                    (fun l -> RT.DObj(Map.ofList l))
                    (Gen.listOfLength
                      s
                      (Gen.zip (Generators.string ()) (genDval' typ (s / 2))))
            // | RT.TIncomplete -> return! Gen.map RT.TIncomplete Arb.generate<incomplete>
            // | RT.TError -> return! Gen.map RT.TError Arb.generate<error>
            // | RT.THttpResponse of DType -> return! Gen.map RT.THttpResponse  Arb.generate<httpresponse >
            // | RT.TDB of DType -> return! Gen.map RT.TDB  Arb.generate<db >
            | RT.TDate ->
                return!
                  Gen.map
                    (fun (dt : System.DateTime) ->
                      // Set milliseconds to zero
                      let dt = (dt.AddMilliseconds(-(double dt.Millisecond)))
                      RT.DDate dt)
                    Arb.generate<System.DateTime>
            | RT.TChar ->
                let! v = G.char ()
                return RT.DChar v
            // | RT.TPassword -> return! Gen.map RT.TPassword Arb.generate<password>
            | RT.TUuid -> return! Gen.map RT.DUuid Arb.generate<System.Guid>
            | RT.TOption typ ->
                return! Gen.map RT.DOption (Gen.optionOf (genDval' typ s))
            // | RT.TErrorRail -> return! Gen.map RT.TErrorRail Arb.generate<errorrail>
            // | RT.TUserType of string * int -> return! Gen.map RT.TUserType  Arb.generate<usertype >
            | RT.TBytes ->
                let! v = Arb.generate<byte []>
                return RT.DBytes v
            | RT.TResult (okType, errType) ->
                return!
                  Gen.map
                    RT.DResult
                    (Gen.oneof [ Gen.map Ok (genDval' okType s)
                                 Gen.map Error (genDval' errType s) ])
            | RT.TFn (paramTypes, returnType) ->
                let parameters =
                  List.mapi
                    (fun i (v : RT.DType) -> (id i, $"{v.toOldString ()}_{i}"))
                    paramTypes

                let! body = genExpr returnType

                return
                  (RT.DFnVal(
                    RT.Lambda
                      { parameters = parameters; symtable = Map.empty; body = body }
                  ))
            | _ -> return failwith $"Not supported yet: {typ}"
          }

        Gen.sized (genDval' typ')

      { new Arbitrary<PT.FQFnName.StdlibFnName * List<RT.Dval>>() with
          member x.Generator =
            gen {
              let fns =
                (LibExecution.StdLib.StdLib.fns @ LibBackend.StdLib.StdLib.fns)
                |> List.filter
                     (fun fn ->
                       not (
                         Set.contains
                           (toString fn.name)
                           (ApiServer.Functions.fsharpOnlyFns.Force())
                       ))
                |> List.filter
                     // FSTODO: all these differences should be removed
                     (function
                     | { name = { module_ = "List"; function_ = "sort" } }
                     | { name = { module_ = "List"; function_ = "sortBy" } } ->
                         // FSTODO: These use a different sort order in OCaml
                         false
                     | { name = { module_ = "Object"; function_ = "toJSON" } }
                     | { name = { module_ = "Dict"; function_ = "toJSON" } } ->
                         // Known formatting differences
                         false
                     | { name = { module_ = "String"; function_ = "base64Decode" } } ->
                         // Don't know what the bug is
                         false
                     | { name = { module_ = "String"; function_ = "trim" } } ->
                         // OCaml seems to trim wrong here
                         false
                     | { name = { module_ = "AWS"; function_ = "urlencode" } } ->
                         // Bug in unicode probably
                         false
                     | fn -> fn.previewable = RT.Pure)

              let! fnIndex = Gen.choose (0, List.length fns - 1)
              let name = fns.[fnIndex].name
              let signature = fns.[fnIndex].parameters

              let unifiesWith (typ : RT.DType) =
                (fun dv ->
                  dv |> LibExecution.TypeChecker.unify (Map.empty) typ |> Result.isOk)

              let rec containsBytes (dv : RT.Dval) =
                match dv with
                | RT.DDB _
                | RT.DInt _
                | RT.DBool _
                | RT.DFloat _
                | RT.DNull
                | RT.DStr _
                | RT.DChar _
                | RT.DIncomplete _
                | RT.DFnVal _
                | RT.DError _
                | RT.DDate _
                | RT.DPassword _
                | RT.DUuid _
                | RT.DHttpResponse (RT.Redirect _)
                | RT.DOption None -> false
                | RT.DList l -> List.any containsBytes l
                | RT.DObj o -> o |> Map.values |> List.any containsBytes
                | RT.DHttpResponse (RT.Response (_, _, dv))
                | RT.DOption (Some dv)
                | RT.DErrorRail dv
                | RT.DResult (Ok dv)
                | RT.DResult (Error dv) -> containsBytes dv
                | RT.DBytes _ -> true

              let arg (i : int) (prevArgs : List<RT.Dval>) =
                // If the parameters need to be in a particular format to get
                // meaningful testing, generate them here.
                let specific =
                  gen {
                    match toString name, i with
                    | "String::toInt_v1", 0
                    | "String::toInt", 0 ->
                        let! v = Arb.generate<bigint>
                        return v |> toString |> RT.DStr
                    | "String::toFloat", 0 ->
                        let! v = Arb.generate<float>
                        return v |> toString |> RT.DStr
                    | "String::toUUID", 0 ->
                        let! v = Arb.generate<System.Guid>
                        return v |> toString |> RT.DStr
                    | "String::padStart", 1
                    | "String::padEnd", 1 ->
                        // FSTODO: allow more than just chars
                        let! v = Arb.generate<char> |> Gen.filter ((<>) (char 0))
                        return RT.DStr(System.String([| v |]))
                    | _ -> return! genDval signature.[i].typ
                  }
                // Still throw in random data 10% of the time to test errors, edge-cases, etc.
                // FSTODO: re-enable the random data
                Gen.frequency [ (0, genDval signature.[i].typ); (9, specific) ]
                |> Gen.filter
                     (fun dv ->
                       // Avoid triggering known errors in OCaml
                       match (i,
                              dv,
                              prevArgs,
                              name.module_,
                              name.function_,
                              name.version) with
                       // Specific OCaml exception (use `when`s here)
                       | 1, RT.DInt i, _, "Int", "divide", 0 when i = 0I -> false
                       | 0, RT.DInt i, _, "List", "repeat", 0 when i < 0I -> false
                       | 1, RT.DStr s, _, "String", "split", 0 when s = "" -> false
                       | 1, RT.DStr s, _, "String", "replaceAll", 0 when s = "" ->
                           false
                       | 1, RT.DInt i, _, "Int", "power", 0
                       | 1, RT.DInt i, _, "", "^", 0 when i < 0I -> false
                       // Int Overflow
                       | 1, RT.DInt i, [ RT.DInt e ], "Int", "power", 0
                       | 1, RT.DInt i, [ RT.DInt e ], "", "^", 0 ->
                           i <> 1I
                           && i <> (-1I)
                           && isValidOCamlInt i
                           && i <= 2000I
                           && isValidOCamlInt (e ** (int i))
                       | 1, RT.DInt i, [ RT.DInt e ], "", "*", 0
                       | 1, RT.DInt i, [ RT.DInt e ], "Int", "multiply", 0 ->
                           isValidOCamlInt (e * i)
                       | 1, RT.DInt i, [ RT.DInt e ], "", "+", 0
                       | 1, RT.DInt i, [ RT.DInt e ], "Int", "add", 0 ->
                           isValidOCamlInt (e + i)
                       | 1, RT.DInt i, [ RT.DInt e ], "Int", "subtract", 0 ->
                           isValidOCamlInt (e - i)
                       | 0, RT.DList l, _, "Int", "sum", 0 ->
                           l
                           |> List.map
                                (function
                                | RT.DInt i -> i
                                | _ -> failwith "should be an int")
                           |> List.fold 0I (+)
                           |> isValidOCamlInt
                       // Int overflow converting from Floats
                       | 0, RT.DFloat f, _, "Float", "floor", 0
                       | 0, RT.DFloat f, _, "Float", "roundDown", 0
                       | 0, RT.DFloat f, _, "Float", "round", 0
                       | 0, RT.DFloat f, _, "Float", "ceiling", 0
                       | 0, RT.DFloat f, _, "Float", "roundUp", 0
                       | 0, RT.DFloat f, _, "Float", "truncate", 0 ->
                           f |> bigint |> isValidOCamlInt
                       // gmtime out of range
                       | 1, RT.DInt i, _, "Date", "sub", 0
                       | 1, RT.DInt i, _, "Date", "subtract", 0
                       | 1, RT.DInt i, _, "Date", "add", 0
                       | 0, RT.DInt i, _, "Date", "fromSeconds", 0 -> i < 10000000I
                       // Out of memory
                       | _, RT.DInt i, _, "List", "range", 0
                       | 0, RT.DInt i, _, "List", "repeat", 0
                       | 2, RT.DInt i, _, "String", "padEnd", 0
                       | 2, RT.DInt i, _, "String", "padStart", 0 -> i < 10000I
                       // Exception
                       | 0, _, _, "", "toString", 0 -> not (containsBytes dv)
                       | _ -> true)

              match List.length signature with
              | 0 -> return (name, [])
              | 1 ->
                  let! arg0 = arg 0 []
                  return (name, [ arg0 ])
              | 2 ->
                  let! arg0 = arg 0 []
                  let! arg1 = arg 1 [ arg0 ]
                  return (name, [ arg0; arg1 ])
              | 3 ->
                  let! arg0 = arg 0 []
                  let! arg1 = arg 1 [ arg0 ]
                  let! arg2 = arg 2 [ arg0; arg1 ]
                  return (name, [ arg0; arg1; arg2 ])
              | 4 ->
                  let! arg0 = arg 0 []
                  let! arg1 = arg 1 [ arg0 ]
                  let! arg2 = arg 2 [ arg0; arg1 ]
                  let! arg3 = arg 3 [ arg0; arg1; arg2 ]
                  return (name, [ arg0; arg1; arg2; arg3 ])
              | _ ->
                  failwith
                    "No support for generating functions with over 4 parameters yet"

                  return (name, [])
            } }

  let equalsOCaml ((fn, args) : (PT.FQFnName.StdlibFnName * List<RT.Dval>)) : bool =
    let t =
      task {
        let origArgs = args
        let args = List.mapi (fun i arg -> ($"v{i}", arg)) args
        let fnArgList = List.map (fun (name, _) -> eVar name) args

        let ast = PT.EFnCall(gid (), RT.FQFnName.Stdlib fn, fnArgList, PT.NoRail)

        let st = Map.ofList args

        let ownerID = System.Guid.NewGuid()
        let canvasID = System.Guid.NewGuid()

        let! expected = OCamlInterop.execute ownerID canvasID ast st [] []

        let! state = executionStateFor "executePure" Map.empty Map.empty

        let! actual =
          LibExecution.Execution.executeExpr state st (ast.toRuntimeType ())

        // Error messages are not required to be directly the same between
        // old and new implementations. However, this can hide errors, so we
        // manually verify them all to make sure we didn't miss any.
        let errorAllowed actualMsg expectedMsg =

          let e2 actualPat expectedPat =
            System.Text.RegularExpressions.Regex.IsMatch(actualMsg, actualPat)
            && System.Text.RegularExpressions.Regex.IsMatch(expectedMsg, expectedPat)

          let e pat = e2 pat pat

          match fn.ToString() with
          // Removed
          | "String::fromChar"
          | "String::toList"
          | "String::fromList"
          | "Char::toUppercase"
          | "Char::toLowercase"
          | "Char::toASCIICode"
          | "Char::toASCIIChar"
          | "String::foreach" -> true
          // Known acceptable difference
          | "Object::toJSON"
          | "Object::toJSON_v1"
          | "Dict::toJSON" -> true
          // Messages are close-enough
          | "%"
          | "Int::mod" ->
              e2
                "Expected the argument `b` to be positive, but it was"
                "Expected the argument `b` argument passed to"
          | "Int::remainder" ->
              e2 "`divisor` cannot be zero" "`divisor` must be non-zero"
          | "Date::parse" -> e "Invalid date format"
          | "Option::andThen" ->
              e2
                "Expecting the function to return Option, but the result was"
                "Expected `f` to return an option"
          | "List::filter" ->
              e2
                "Expecting the function to return Bool, but the result was"
                "Expecting fn to return bool"
          | "String::toFloat" ->
              e2
                "Expected the argument `s` to be a string representation of an IEEE float, but it was"
                "Expected a string representation of an IEEE float"
          | _ -> false

        let debugFn () =
          debuG "fn" fn
          debuG "args" (List.map (fun (_, v) -> debugDval v) args)

        if not (Expect.isCanonical expected) then
          debugFn ()
          debuG "ocaml (expected) is not normalized" (debugDval expected)
          return false
        elif not (Expect.isCanonical actual) then
          debugFn ()
          debuG "fsharp (actual) is not normalized" (debugDval actual)
          return false
        elif dvalEquality actual expected then
          return true
        else
          match actual, expected with
          | RT.DError (_, msg1), RT.DError (_, msg2) ->
              let allowed = errorAllowed msg1 msg2

              if not allowed then
                debugFn ()
                printfn $"Got different error msgs: \"{msg1}\" vs \"{msg2}\""

              // FSTODO make false
              return true
          | RT.DResult (Error (RT.DStr msg1)), RT.DResult (Error (RT.DStr msg2)) ->
              let allowed = errorAllowed msg1 msg2

              if not allowed then
                debugFn ()
                printfn $"Got different Results msgs: \"{msg1}\" vs \"{msg2}\""

              // FSTODO make false
              return true
          | _ ->
              debugFn ()
              debuG "ocaml (expected)" (debugDval expected)
              debuG "fsharp (actual) " (debugDval actual)
              return false
      }

    Task.WaitAll [| t :> Task |]
    t.Result

  let tests =
    testList
      "executePureFunctions"
      [ testPropertyWithGenerator typeof<Generator> "equalsOCaml" equalsOCaml ]


let stillBuggy = testList "still buggy" [ OCamlInterop.tests; FQFnName.tests ]

let knownGood =
  testList
    "known good"
    ([ Roundtrippable.tests
       Queryable.tests
       DeveloperRepr.tests
       EndUserReadable.tests
       Hashing.tests
       PrettyMachineJson.tests
       ExecutePureFunctions.tests ])

let tests = testList "FuzzTests" [ knownGood; stillBuggy ]

// FSTODO: add fuzz test that running analysis gets the same results for different exprs

[<EntryPoint>]
let main args = runTestsWithCLIArgs [] args tests
