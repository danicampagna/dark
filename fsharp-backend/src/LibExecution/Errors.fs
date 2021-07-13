module LibExecution.Errors

// Reusable errors and messages used mostly in the standard library

open Prelude
open RuntimeTypes

// ------------------
// Exception types
// ------------------

type StdlibError =
  | FunctionRemoved
  | IncorrectArgs
  // When we encounter a fakeDval, this exception allows us to jump out of the
  // computation immediately, and the caller can return the dval. This is useful
  // for jumping out of folds and other complicated constructs.
  | FakeDvalFound of Dval
  | StringError of string

// Error made in a standard library call. This allows us to call them when we
// don't have the information to make a RuntimeException, and they are
// converted to runtimeExceptions at the call site.
exception StdlibException of StdlibError

// Error in DB::query when we don't support something yet
exception DBQueryException of string

// Error in DB::query when there's a fakeval in the query
exception FakeValFoundInQuery of Dval



// ------------------
// Messages
// ------------------
let expectedLambdaType (typ : DType) (actual : Dval) : string =
  let actual = DvalRepr.toDeveloperReprV0 actual
  let typ = DvalRepr.typeToDeveloperReprV0 typ
  $"Expecting the function to return {typ}, but the result was {actual}"

let expectedLambdaValue (expected : string) (actual : Dval) : string =
  let actual = DvalRepr.toDeveloperReprV0 actual
  $"Expecting the function to return {expected}, but the result was {actual}"



// Used for values which are outside the range of expected values for some
// reason. Really, any function using this should have a Result type instead.
let argumentWasnt (expected : string) (paramName : string) (dv : Dval) : string =
  let actual = DvalRepr.toDeveloperReprV0 dv
  $"Expected the argument `{paramName}` to be {expected}, but it was `{actual}`"

let dividingByZero (paramName : string) : string = $"`{paramName}` cannot be zero"

let queryCompilerErrorTemplate =
  "You're using our new experimental Datastore query compiler. It compiles your lambdas into optimized (and partially indexed) Datastore queries, which should be reasonably faster.\n\nUnfortunately, we hit a snag while compiling your lambda. We only support a subset of Dark's functionality, but will be expanding it in the future.\n\nSome Dark code is not supported in DB::query lambdas for now, and some of it won't be supported because it's an odd thing to do in a datastore query. If you think your operation should be supported, let us know in #general.\n\nError: "

let typeErrorMsg (colName : string) (expected : DType) (actual : Dval) : string =
  let expected = DvalRepr.typeToDeveloperReprV0 expected
  let actualType = DvalRepr.prettyTypename actual

  $"Expected a value of type {expected} but got a {actualType} (`{actual}`)"
  + $" in column {colName}"

// ------------------
// Extremely common exceptions
// ------------------
let throw (str : string) : 'a = raise (StdlibException(StringError str))

// When a function in called with the wrong number of arguments. Used in almost every function signature.
let incorrectArgs () = raise (StdlibException IncorrectArgs)

let incorrectArgsMsg (name : FQFnName.T) (p : Param) (actual : Dval) : string =
  let actualRepr = DvalRepr.toDeveloperReprV0 actual
  let actualType = Dval.toType actual
  let actualTypeRepr = DvalRepr.typeToDeveloperReprV0 actualType
  let expectedTypeRepr = DvalRepr.typeToDeveloperReprV0 p.typ
  let fnname = name.ToString()

  let conversionMsg =
    match p.typ, actualType, name with
    | TInt, TFloat, FQFnName.Stdlib std when std.module_ = "Int" ->
        let altfn = { std with module_ = "Float" }

        $" Try using {altfn.ToString()}, or use Float::truncate to truncate Floats to Ints."
    | TInt, TStr, FQFnName.Stdlib std when
      std.module_ = "Int" && std.function_ = "add" -> " Use ++ to concatenate"
    | _ -> ""

  $"{fnname} was called with a {actualTypeRepr} ({actualRepr}), but `{p.name}` expected "
  + $"an {expectedTypeRepr}.{conversionMsg}"


// When a function has been removed (rarely happens but does happen occasionally)
let removedFunction () : 'b = raise (StdlibException FunctionRemoved)

// When you have a fakeval, you typically just want to return it.
let foundFakeDval (dv : Dval) : 'a = raise (StdlibException(FakeDvalFound dv))
