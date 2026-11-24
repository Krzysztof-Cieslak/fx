/// Exceptions: F# exception types, system exceptions (including hardware faults), handlers and cleanup.
module Fx.Aot.Scenarios.Exceptions

open System
open System.Collections.Generic

exception ParseError of message: string * line: int

exception Cancelled

type Resource(name: string, log: ResizeArray<string>) =
    do log.Add $"open {name}"

    interface IDisposable with
        member _.Dispose() = log.Add $"dispose {name}"

/// Runs `f` and describes the exception it throws, without stack traces (they differ by design).
let describe (f: unit -> 'T) : string =
    try
        let value = f ()
        $"no exception: {value}"
    with e ->
        $"{e.GetType().FullName}: {Show.escape e.Message}"

/// Like `describe`, for exceptions whose message differs under Native AOT: names only the type.
let describeType (f: unit -> 'T) : string =
    try
        let value = f ()
        $"no exception: {value}"
    with e ->
        e.GetType().FullName |> Show.orNull

let private zero () = 0
let private nullString () : string = Unchecked.defaultof<string>

let systemExceptions =
    Scenario.lines "system-exceptions" (fun () ->
        let arr = [| 1; 2; 3 |]

        [
            describe (fun () -> failwith "boom")
            describe (fun () -> invalidArg "count" "must be positive")
            describe (fun () -> invalidOp "not now")
            describe (fun () -> nullArg "input")
            describe (fun () -> raise (NotSupportedException()))
            describe (fun () -> 10 / zero ())
            describe (fun () -> (nullString ()).Length)
            describe (fun () -> arr[5])
            describe (fun () -> Map.find "missing" (Map [ "a", 1 ]))
            describe (fun () -> Dictionary<string, int>()["missing"])
            describe (fun () -> List.head ([]: int list))
            describe (fun () -> Checked.(+) Int32.MaxValue 1)
            // Native AOT drops the type names from the message ("Specified cast is not valid.").
            describeType (fun () -> unbox<string>(box 1))
            describe (fun () -> Int32.Parse "x")
            describe (fun () -> Seq.find (fun x -> x > 10) [ 1; 2 ])
        ])

let fsharpExceptions =
    Scenario.lines "fsharp-exceptions" (fun () ->
        let handle f =
            try
                f ()
            with
            | ParseError(msg, line) -> $"parse error '{msg}' at line {line}"
            | Cancelled -> "cancelled"
            | :? ArgumentException as e when e.ParamName = "x" -> $"argument x: {e.GetType().Name}"
            | :? AggregateException as e -> $"aggregate of {e.Flatten().InnerExceptions.Count}"
            | e -> $"other {e.GetType().Name}"

        [
            handle (fun () -> raise (ParseError("unexpected token", 3)))
            handle (fun () -> raise Cancelled)
            handle (fun () -> invalidArg "x" "bad")
            handle (fun () -> invalidArg "y" "bad")
            handle (fun () ->
                raise (AggregateException(InvalidOperationException(), AggregateException(TimeoutException()))))
            $"""F# exception equality = {ParseError("a", 1) = ParseError("a", 1)}"""
            $"""F# exception type = {ParseError("a", 1).GetType().Name}"""
            describe (fun () -> raise (ParseError("in describe", 7)))
        ])

let handlers =
    Scenario.lines "exception-handlers" (fun () ->
        let log = ResizeArray<string>()

        let nested () =
            try
                try
                    use _r = new Resource("inner", log)
                    log.Add "body"
                    failwith "first"
                finally
                    log.Add "finally 1"
            with e ->
                log.Add $"caught {e.Message}"

                try
                    reraise ()
                finally
                    log.Add "finally 2"

        let outcome =
            try
                nested ()
                "returned"
            with e ->
                $"rethrown {e.Message}"

        let wrapped =
            try
                try
                    failwith "root cause"
                with e ->
                    raise (InvalidOperationException("wrapper", e))
            with e ->
                let inner =
                    match e.InnerException with
                    | null -> "none"
                    | inner -> inner.Message

                $"{e.Message} <- {inner}"

        let result =
            [ "1"; "x"; "3" ]
            |> List.map (fun s ->
                try
                    Ok(int s)
                with _ ->
                    Error s)

        [ yield! log; outcome; wrapped; $"results = {result}" ])

let scenarios = [ systemExceptions; fsharpExceptions; handlers ]
