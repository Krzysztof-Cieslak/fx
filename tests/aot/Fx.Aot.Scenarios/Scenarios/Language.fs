/// Unions, records, ToString, pattern matching and recursion.
module Fx.Aot.Scenarios.Language

open Fx.Aot.Scenarios.Show

type Shape =
    | Circle of radius: float
    | Rect of width: float * height: float
    | Empty

    override this.ToString() =
        match this with
        | Circle r -> $"Circle({r})"
        | Rect(w, h) -> $"Rect({w}x{h})"
        | Empty -> "Empty"

type Expr =
    | Num of int
    | Add of Expr * Expr
    | Mul of Expr * Expr
    | Neg of Expr

    override this.ToString() =
        match this with
        | Num n -> string n
        | Add(a, b) -> $"({a} + {b})"
        | Mul(a, b) -> $"({a} * {b})"
        | Neg e -> $"-{e}"

[<Struct>]
type Tag =
    | Tag of string

    override this.ToString() =
        let (Tag t) = this
        "#" + t

type Labelled =
    {
        Label: string
        Shape: Shape
        Tags: Map<string, int>
        Origin: int * int
    }

    override this.ToString() =
        let tags =
            this.Tags
            |> Map.toList
            |> List.map (fun (k, v) -> $"{k}:{v}")
            |> String.concat ","

        let x, y = this.Origin
        $"{this.Label} {this.Shape} at ({x}, {y}) [{tags}]"

/// A record of primitives without a ToString override: prints its type name under --reflectionfree.
type Point = { X: int; Y: int }

let rec eval expr =
    match expr with
    | Num n -> n
    | Add(a, b) -> eval a + eval b
    | Mul(a, b) -> eval a * eval b
    | Neg e -> -(eval e)

let (|Even|Odd|) n = if n % 2 = 0 then Even else Odd

let (|Prefix|_|) (prefix: string) (s: string) =
    if s.StartsWith(prefix, System.StringComparison.Ordinal) then
        Some(s.Substring prefix.Length)
    else
        None

[<Measure>]
type m

[<Measure>]
type s

let unionsAndRecords =
    Scenario.lines "unions-and-records" (fun () ->
        let shapes = [ Circle 1.5; Rect(2.0, 3.0); Empty ]
        let expr = Add(Num 1, Mul(Num 2, Neg(Num 3)))

        let labelled =
            {
                Label = "a"
                Shape = Rect(1.0, 2.5)
                Tags = Map [ "z", 1; "a", 2 ]
                Origin = (3, -4)
            }

        let updated = { labelled with Shape = Empty }
        let point = { X = 1; Y = 2 }
        let anonymous = {| Name = "anon"; Count = 2 |}

        [
            yield! shapes |> List.map (fun s -> $"shape {s} / {s.ToString()} / {string s}")
            $"shapes list = {shapes}"
            $"expr = {expr} = {eval expr}"
            $"""tag = {Tag "x"}"""
            $"labelled = {labelled}"
            $"updated = {updated}"
            $"is circle = {(Circle 1.0).IsCircle}, is empty = {Empty.IsEmpty}"
            $"record without ToString = {point}"
            kv "point type name" (point.GetType().Name)
            $"anonymous record field = {anonymous.Name}, {anonymous.Count}"
        ])

let optionsAndResults =
    Scenario.lines "options-and-results" (fun () ->
        let parse (s: string) =
            match System.Int32.TryParse s with
            | true, n -> Ok n
            | _ -> Error $"not a number: {s}"

        let describe r =
            match r with
            | Ok n -> $"ok {n}"
            | Error e -> $"error {e}"

        [
            $"some = {Some 1}, none = {(None: int option)}, value some = {ValueSome 2}"
            $"option map/bind = {Some 3
                                 |> Option.map ((+) 1)
                                 |> Option.bind (fun x -> if x > 3 then Some(x * 2) else None)}"
            $"defaultValue = {None |> Option.defaultValue 7}"
            $"ok = {(Ok 1: Result<int, string>)}"
            describe (parse "42")
            describe (parse "x")
            $"""result chain = {parse "5"
                                |> Result.map ((*) 2)
                                |> Result.bind (fun n -> if n > 5 then Ok n else Error "small")}"""
            $"choice = {(Choice1Of2 1: Choice<int, string>)}"
            $"""tuple = {(1, "two", 3.0)}, struct tuple = {struct (4, 5)}"""
            $"unit = [{()}]"
        ])

let patterns =
    Scenario.lines "pattern-matching" (fun () ->
        let classify n =
            match n with
            | 0 -> "zero"
            | Even when n < 0 -> "negative even"
            | Even -> "even"
            | Odd -> "odd"

        let route (path: string) =
            match path with
            | Prefix "/api/" rest -> $"api:{rest}"
            | Prefix "/" rest when rest.Length > 0 -> $"page:{rest}"
            | _ -> "root"

        let firstTwo xs =
            match xs with
            | [] -> "empty"
            | [ x ] -> $"one {x}"
            | x :: y :: _ -> $"two {x} {y}"

        let arrays a =
            match a with
            | [| a; b |] -> $"pair {a},{b}"
            | [||] -> "none"
            | _ -> $"len {a.Length}"

        [
            yield! [ -4; -3; 0; 1; 2 ] |> List.map (fun n -> $"{n} is {classify n}")
            yield! [ "/api/users"; "/home"; "/" ] |> List.map route
            firstTwo []
            firstTwo [ 1 ]
            firstTwo [ 1; 2; 3 ]
            arrays [| 1; 2 |]
            arrays [||]
            arrays [| 1; 2; 3 |]
            $"speed = {10.0<m> / 4.0<s>}"
        ])

let rec private isEven n = if n = 0 then true else isOdd (n - 1)
and private isOdd n = if n = 0 then false else isEven (n - 1)

let rec private sumCps (xs: int list) (k: int64 -> int64) =
    match xs with
    | [] -> k 0L
    | x :: rest -> sumCps rest (fun acc -> k (acc + int64 x))

let rec private loop i acc =
    if i = 0 then acc else loop (i - 1) (acc + int64 i)

/// Deep recursion that relies on F# tail calls (`.tail` prefixes) rather than the stack.
let tailCalls =
    Scenario.lines "tail-calls" (fun () ->
        let depth = 1_000_000

        [
            $"self-recursive loop = {loop depth 0L}"
            $"mutual recursion isEven {depth} = {isEven depth}"
            $"continuation-passing sum = {sumCps [ 1..depth ] id}"
        ])

let scenarios = [ unionsAndRecords; optionsAndResults; patterns; tailCalls ]
