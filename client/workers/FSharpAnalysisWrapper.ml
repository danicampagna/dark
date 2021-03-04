open Prelude

external rollbarConfig : string = "rollbarConfig" [@@bs.val]

let () = Rollbar.init (Json.parseOrRaise rollbarConfig)

type event = < data : Types.performAnalysisParams Js.nullable [@bs.get] > Js.t

type self

external self : self = "self" [@@bs.val]

external onmessage : self -> (event -> unit) -> unit = "onmessage" [@@bs.set]

external postMessage : self -> Types.performAnalysisResult -> unit
  = "postMessage"
  [@@bs.send]

type dotnet =
  < invokeMethodAsync : string -> string -> string -> string * string [@bs.meth] >
  Js.t

external dotnet : dotnet = "dotnet" [@@bs.val]

let () =
  onmessage self (fun event ->
      let result =
        (* TODO: couldn't make Tc work *)
        match Js.Nullable.toOption event##data with
        | None ->
            (* When we sent too much data, the event just won't have data in it. *)
            reportError "Trace was too big to load into analysis" () ;
            Belt.Result.Error
              (Types.AnalysisParseError
                 "Trace was too big to load into analysis")
        | Some (AnalyzeHandler hParams as params) ->
            let encoded =
              Js.Json.stringify (Encoders.performHandlerAnalysisParams hParams)
            in
            let success, msg =
              dotnet##invokeMethodAsync
                "FSharpAnalysis"
                "performHandlerAnalysis"
                encoded
            in
            if success = "success"
            then Belt.Result.Ok msg
            else
              (* This is not nearly as close to the original Stack Overflow
               * error as I'd like.  I can write
               * Analysis_types.function_result_of_yojson, and
               * confirm with logs that the error occurs in the dval_of_yojson
               * part of `j |> index 3 |> dval_of_yojson`; but wrapping
               * dval_of_yojson in a try/with does not catch the error. We have
               * seen the two messages below on a large DList (because it
               * contains a list and of_yojson maybe isn't tail-recursive -
               * though to_yojson is now:
               * https://github.com/ocaml-community/yojson/issues/47), but it's
               * not impossible that other code could also cause an overflow. *)
              let handler_spec_string =
                let spec = hParams.handler.spec in
                [spec.space; spec.name; spec.modifier]
                |> List.map ~f:(function Types.F (_, s) -> s | _ -> "_")
                |> List.filter ~f:(( <> ) "_")
                |> fun ss -> "(" ^ String.join ~sep:", " ss ^ ")"
              in
              let msg =
                if msg = "(\"Stack overflow\")"
                   || msg
                      = "(\"SyntaxError: Invalid regular expression: /maximum call stack/: Maximum call stack size exceeded\")"
                then
                  "Analysis results are too big to send back to the editor "
                  ^ handler_spec_string
                else msg
              in
              reportError "An execution failure occurred in a handler" msg ;
              Belt.Result.Error (Types.AnalysisExecutionError (params, msg))
        | Some (AnalyzeFunction fParams as params) ->
            let encoded =
              Js.Json.stringify (Encoders.performFunctionAnalysisParams fParams)
            in
            let success, msg =
              dotnet##invokeMethodAsync
                "FSharpAnalysis"
                "performFunctionAnalysis"
                encoded
            in
            if success = "success"
            then Belt.Result.Ok msg
            else (
              reportError "An execution failure occurred in a function" msg ;
              Belt.Result.Error (Types.AnalysisExecutionError (params, msg)) )
      in
      let decoded =
        Tc.Result.andThen
          ~f:(fun res ->
            try
              Belt.Result.Ok (Decoders.analysisEnvelope (Json.parseOrRaise res))
            with Js.Exn.Error err ->
              let msg =
                err
                |> Js.Exn.message
                |> Tc.Option.withDefault ~default:"Unknown parse error"
              in
              reportError "Parse error in analysisWrapper" msg ;
              Belt.Result.Error (Types.AnalysisParseError msg))
          result
      in
      postMessage self decoded)
