/// Generics: inline functions, constraints, generic virtual methods over value types, type tests.
module Fx.Aot.Scenarios.Generics

open System
open System.Collections.Generic

[<Struct>]
type Pair<'a, 'b> =
    {
        First: 'a
        Second: 'b
    }

    override this.ToString() = $"<{this.First}, {this.Second}>"

/// A generic method on an interface: under Native AOT every instantiation must be known statically.
type IDescriber =
    abstract Describe<'T> : 'T -> string

type TypeNameDescriber() =
    interface IDescriber with
        member _.Describe<'T>(value: 'T) = $"{typeof<'T>.Name}:{value}"

/// A generic class with per-instantiation static state.
type Counter<'T>() =
    static let mutable created = 0

    do created <- created + 1

    static member Created = created

type Animal =
    abstract Sound: string

type Dog() =
    interface Animal with
        member _.Sound = "woof"

type Cat() =
    interface Animal with
        member _.Sound = "meow"

let inline add a b = a + b

let inline sumAll (xs: ^T list) =
    List.fold (+) LanguagePrimitives.GenericZero xs

let largest (xs: 'T list) : 'T when 'T: comparison = List.max xs

let describeAll (d: IDescriber) =
    [
        d.Describe 42
        d.Describe 2.5
        d.Describe "text"
        d.Describe { First = 1; Second = 'c' }
        d.Describe(struct (1, 2L))
        d.Describe [ 1uy; 2uy ]
        d.Describe(Some DayOfWeek.Friday)
    ]

let classify (value: obj | null) =
    match value with
    | null -> "null"
    | :? int as i -> $"int {i}"
    | :? string as s -> $"string {s}"
    | :? (int list) as l -> $"int list of {l.Length}"
    | :? IEnumerable<char> -> "chars"
    | :? Pair<int, string> as p -> $"pair {p}"
    | other -> $"other {other.GetType().Name}"

let generics =
    Scenario.lines "generics" (fun () ->
        let describer = TypeNameDescriber() :> IDescriber

        let ints, strings, floats =
            Counter<int>.Created, Counter<string>.Created, Counter<float>.Created

        let _ = Counter<int>()
        let _ = Counter<int>()
        let _ = Counter<string>()
        let animals: Animal list = [ Dog(); Cat() ]
        let lazyValue = lazy (add 20 22)

        let values: (obj | null) list =
            [
                box 1
                box "s"
                box [ 1; 2 ]
                box "chars"
                box { First = 1; Second = "x" }
                box 2.0
                null
            ]

        let custom =
            { new Object() with
                member _.ToString() = "custom"
            }

        let byLength =
            { new IComparer<string> with
                member _.Compare(a, b) =
                    compare (nonNull a).Length (nonNull b).Length
            }

        [
            $"""inline add = {add 1 2}, {add 1.5 2.25}, {add 1.1M 2.2M}, {add "a" "b"}"""
            $"inline sum = {sumAll [ 1; 2; 3 ]}, {sumAll [ 0.5; 0.25 ]}, {sumAll [ 1L; 2L ]}"
            $"""largest = {largest [ 3; 9; 2 ]}, {largest [ "b"; "c"; "a" ]}, {largest [ (1, 'z'); (2, 'a') ]}"""
            yield! describeAll describer
            $"counters = int +{Counter<int>.Created - ints}, string +{Counter<string>.Created - strings}, float +{Counter<float>.Created - floats}"
            $"defaults = {Unchecked.defaultof<int>}, {isNull Unchecked.defaultof<string | null>}, {Unchecked.defaultof<Pair<int, bool>>}"
            $"type names = {typeof<int list>.Name}, {typeof<Map<string, int>>.Name}, {typeof<Pair<int, string>>.Name}"
            $"generic type definition = {typedefof<Pair<_, _>>.Name}, args = {typeof<Pair<int, string>>.GetGenericArguments().Length}"
            yield! values |> List.map classify
            $"interfaces = {animals |> List.map (fun a -> a.Sound)}"
            $"lazy = {lazyValue.IsValueCreated}, {lazyValue.Value}, {lazyValue.IsValueCreated}"
            $"""object expressions = {custom}, {List.sortWith (fun a b -> byLength.Compare(a, b)) [ "ccc"; "a"; "bb" ]}"""
        ])

let scenarios = [ generics ]
