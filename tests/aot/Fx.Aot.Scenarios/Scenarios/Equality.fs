/// Structural equality, comparison and hashing.
module Fx.Aot.Scenarios.Equality

open System
open System.Collections.Generic

type Version =
    {
        Major: int
        Minor: int
        Label: string
    }

    override this.ToString() =
        $"{this.Major}.{this.Minor}-{this.Label}"

type Priority =
    | Low
    | Medium of int
    | High of reason: string * level: int

    override this.ToString() =
        match this with
        | Low -> "Low"
        | Medium n -> $"Medium {n}"
        | High(r, l) -> $"High {r} {l}"

[<Struct>]
type Cell = { Row: int; Col: int }

[<CustomEquality; CustomComparison>]
type CaseInsensitive =
    | CaseInsensitive of string

    member this.Key =
        let (CaseInsensitive s) = this
        s.ToUpperInvariant()

    override this.Equals(other: obj) =
        match other with
        | :? CaseInsensitive as o -> this.Key = o.Key
        | _ -> false

    override this.GetHashCode() = this.Key.Length

    override this.ToString() =
        let (CaseInsensitive s) = this
        s

    interface IComparable with
        member this.CompareTo(other: obj) =
            match other with
            | :? CaseInsensitive as o -> String.CompareOrdinal(this.Key, o.Key)
            | _ -> invalidArg "other" "not comparable"

[<ReferenceEquality>]
type Handle = { Id: int }

let structural =
    Scenario.lines "structural-equality" (fun () ->
        let v1 = { Major = 1; Minor = 2; Label = "a" }
        let v2 = { Major = 1; Minor = 2; Label = "a" }
        let v3 = { v1 with Minor = 10 }
        let h1 = { Id = 1 }
        let h2 = { Id = 1 }
        let c1: Cell = { Row = 1; Col = 2 }
        let c2: Cell = { Row = 1; Col = 2 }

        [
            $"records equal = {v1 = v2}, different = {v1 = v3}, physical = {LanguagePrimitives.PhysicalEquality v1 v2}"
            $"""unions equal = {High("x", 1) = High("x", 1)}, different = {Medium 1 = Medium 2}"""
            $"""tuples = {(1, "a") = (1, "a")}, lists = {[ 1; 2 ] = [ 1; 2 ]}, arrays = {[| 1; 2 |] = [| 1; 2 |]}"""
            $"nested = {Some [ v1 ] = Some [ v2 ]}, maps = {Map [ 1, v1 ] = Map [ 1, v2 ]}"
            $"struct records = {c1 = c2}"
            $"""custom equality = {CaseInsensitive "abc" = CaseInsensitive "ABC"}"""
            $"reference equality = {h1 = h1}, {h1 = h2}"
            $"nan = {nan = nan}, [nan] = {[ nan ] = [ nan ]}, nan.Equals = {nan.Equals nan}"
            $"0.0 = -0.0: {0.0 = -0.0}"
        ])

let comparison =
    Scenario.lines "structural-comparison" (fun () ->
        let versions =
            [
                { Major = 2; Minor = 0; Label = "b" }
                { Major = 1; Minor = 10; Label = "a" }
                { Major = 1; Minor = 2; Label = "z" }
                { Major = 1; Minor = 2; Label = "c" }
            ]

        let priorities = [ High("b", 1); Low; Medium 5; High("a", 2); Medium 1 ]
        let cells = [ { Row = 2; Col = 1 }; { Row = 1; Col = 3 }; { Row = 1; Col = 2 } ]

        let words =
            [
                CaseInsensitive "beta"
                CaseInsensitive "Alpha"
                CaseInsensitive "ALPHA"
                CaseInsensitive "gamma"
            ]

        [
            $"sorted versions = {List.sort versions}"
            $"max version = {List.max versions}, min = {List.min versions}"
            $"sorted priorities = {List.sort priorities}"
            $"""compare unions = {compare Low (Medium 0)}, {compare (High("a", 9)) (High("b", 0))}"""
            $"""sorted struct records = {cells |> List.sort |> List.map (fun c -> $"{c.Row}:{c.Col}")}"""
            $"sorted custom = {List.sort words}, distinct = {List.distinct words}"
            $"""compare tuples = {compare (1, "b") (1, "a")}, lists = {compare [ 1; 2 ] [ 1; 2; 0 ]}"""
            $"""compare arrays = {compare [| 1; 3 |] [| 1; 2; 9 |]}, strings = {compare "B" "a"}"""
            $"compare options = {compare (Some 1) None}, {compare None (Some 0)}"
            $"compare nan = {compare nan 1.0}, {compare 1.0 nan}, sort = {List.sort [ 2.0; nan; 1.0 ]}"
            $"sortDescending = {List.sortDescending [ 3; 1; 2 ]}"
        ])

let hashing =
    Scenario.lines "structural-hashing" (fun () ->
        // String hash codes are randomized per process; only hash values without strings.
        let dict = Dictionary<Cell, string>(HashIdentity.Structural)
        dict[{ Row = 1; Col = 1 }] <- "a"
        dict[{ Row = 1; Col = 2 }] <- "b"
        dict[{ Row = 1; Col = 1 }] <- "c"

        let set = HashSet<int * int>(HashIdentity.Structural)

        for i in 0..20 do
            set.Add((i % 5, i % 3)) |> ignore

        let cell: Cell = { Row = 3; Col = 4 }

        [
            $"hash int = {hash 42}, hash tuple = {hash (1, 2)}, hash list = {hash [ 1; 2; 3 ]}"
            $"hash struct record = {hash cell}, hash union = {hash (Medium 7)}"
            $"hash float = {hash 1.5}, hash bool = {hash true}, hash char = {hash 'x'}"
            $"hash equal values equal = {hash [ Some 1 ] = hash [ Some 1 ]}"
            $"""dictionary = {dict
                              |> Seq.map (fun kv -> $"{kv.Key.Row},{kv.Key.Col}={kv.Value}")
                              |> List.ofSeq}"""
            $"hashset count = {set.Count}, first = {Seq.head set}"
        ])

let scenarios = [ structural; comparison; hashing ]
