/// FSharp.Core and BCL collections: Map, Set, List, Seq, Array and mutable collections.
module Fx.Aot.Scenarios.Collections

open System.Collections.Generic

type Color =
    | Red
    | Green
    | Blue

    override this.ToString() =
        match this with
        | Red -> "red"
        | Green -> "green"
        | Blue -> "blue"

let mapsAndSets =
    Scenario.lines "maps-and-sets" (fun () ->
        let m = Map [ "banana", 3; "apple", 1; "cherry", 7; "Apple", 0 ]

        let m2 =
            m
            |> Map.add "date" 4
            |> Map.remove "banana"
            |> Map.change "apple" (Option.map ((+) 10))

        let byColor = Map [ Blue, 3; Red, 1; Green, 2 ]
        let s1 = Set [ 5; 1; 3; 9; 1 ]
        let s2 = Set [ 3; 4; 5 ]
        let colors = Set [ Blue; Red; Blue ]

        [
            $"map = {m}"
            $"map toList = {Map.toList m2}"
            $"""tryFind = {Map.tryFind "cherry" m}, {Map.tryFind "fig" m}, containsKey = {m.ContainsKey "apple"}"""
            $"fold = {Map.fold (fun acc (k: string) v -> acc + k.Length * v) 0 m}, foldBack = {Map.foldBack (fun k _ acc -> k :: acc) m []}"
            $"filter = {m |> Map.filter (fun _ v -> v > 1) |> Map.keys |> List.ofSeq}"
            $"partition = {m
                           |> Map.partition (fun (k: string) _ -> k.StartsWith 'a')
                           |> fun (a, b) -> (a.Count, b.Count)}"
            $"union keys = {byColor |> Map.toList}, min key = {byColor |> Seq.head |> fun kv -> kv.Key}"
            $"set = {s1}, union = {Set.union s1 s2}, intersect = {Set.intersect s1 s2}, difference = {s1 - s2}"
            $"subset = {Set.isSubset (Set [ 1; 3 ]) s1}, min = {s1.MinimumElement}, max = {s1.MaximumElement}"
            $"union set = {colors}, count = {colors.Count}, contains green = {colors.Contains Green}"
            $"map of sets = {Map [ 1, Set [ 'b'; 'a' ] ] |> Map.map (fun _ s -> Set.toList s)}"
        ])

let lists =
    Scenario.lines "lists" (fun () ->
        let xs = [ 5; 3; 8; 1; 9; 2; 7 ]

        let comprehension =
            [
                for i in 1..4 do
                    if i % 2 = 0 then yield i * 10 else yield! [ i; -i ]
            ]

        [
            $"list = {xs}, comprehension = {comprehension}"
            $"map/filter = {xs |> List.map (fun x -> x * 2) |> List.filter (fun x -> x > 6)}"
            $"collect = {[ 1; 2; 3 ] |> List.collect (fun x -> List.replicate x x)}"
            $"groupBy = {xs |> List.groupBy (fun x -> x % 3)}, countBy = {xs |> List.countBy (fun x -> x > 4)}"
            $"""sortBy = {xs |> List.sortBy (fun x -> -x)}, sortWith = {[ "bb"; "a"; "ccc"; "dd" ]
                                                                        |> List.sortWith (fun a b -> compare a.Length b.Length)}"""
            $"distinctBy = {xs |> List.distinctBy (fun x -> x % 4)}, chunkBySize = {List.chunkBySize 3 xs}"
            $"windowed = {List.windowed 3 [ 1..5 ]}, pairwise = {List.pairwise [ 1..4 ]}"
            $"""zip = {List.zip [ 1; 2 ] [ "a"; "b" ]}, unzip = {List.unzip [ (1, 'a'); (2, 'b') ]}"""
            $"fold = {List.fold (fun acc x -> acc * 10 + x) 0 [ 1; 2; 3 ]}, scan = {List.scan (+) 0 [ 1; 2; 3 ]}"
            $"reduce = {List.reduce max xs}, sum = {List.sum xs}, average = {List.averageBy float xs}"
            $"partition = {List.partition (fun x -> x % 2 = 0) xs}, splitAt = {List.splitAt 2 xs}"
            $"tryPick = {xs |> List.tryPick (fun x -> if x > 7 then Some(x * 100) else None)}, exists = {List.exists ((=) 9) xs}"
            $"indexed = {List.indexed [ 'a'; 'b' ]}, transpose = {List.transpose [ [ 1; 2 ]; [ 3; 4 ] ]}"
            $"allPairs = {List.allPairs [ 1; 2 ] [ 'x'; 'y' ]}, except = {List.except [ 3; 8 ] xs}"
            $"rev = {List.rev xs}, take/skip = {xs |> List.skip 2 |> List.take 3}, last = {List.last xs}"
            $"long list = {List.init 100_000 id |> List.map (fun x -> int64 x) |> List.sum}"
        ])

let sequences =
    Scenario.lines "sequences" (fun () ->
        let log = ResizeArray<string>()

        let traced =
            seq {
                try
                    for i in 1..10 do
                        log.Add $"yield {i}"
                        yield i
                finally
                    log.Add "disposed"
            }

        let firstThree = traced |> Seq.map (fun x -> x * x) |> Seq.take 3 |> List.ofSeq
        let fib = Seq.unfold (fun (a, b) -> Some(a, (b, a + b))) (0L, 1L)
        let cached = Seq.cache (seq { for i in 1..3 -> i * 7 })

        [
            $"first three squares = {firstThree}"
            yield! log
            $"fib = {fib |> Seq.take 12 |> List.ofSeq}, 90th = {Seq.item 90 fib}"
            $"takeWhile = {Seq.unfold (fun i -> Some(i, i + 1)) 0
                           |> Seq.takeWhile (fun x -> x < 5)
                           |> List.ofSeq}"
            $"cached = {List.ofSeq cached}, again = {List.ofSeq cached}"
            $"groupBy = {seq { 1..10 }
                         |> Seq.groupBy (fun x -> x % 3)
                         |> Seq.map (fun (k, v) -> k, List.ofSeq v)
                         |> List.ofSeq}"
            $"sortBy stable = {[ (1, 'b'); (0, 'z'); (1, 'a'); (0, 'y') ] |> Seq.sortBy fst |> List.ofSeq}"
            $"distinct = {Seq.distinct [ 3; 1; 3; 2; 1 ] |> List.ofSeq}, zip3 = {Seq.zip3 [ 1 ] [ 'a' ] [ true ] |> List.ofSeq}"
            $"seq ToString = {seq { 1..3 }}"
        ])

let arrays =
    Scenario.lines "arrays-and-mutable-collections" (fun () ->
        let arr = [| 9; 4; 7; 1 |]
        Array.sortInPlace arr
        let grid = Array2D.init 2 3 (fun r c -> r * 10 + c)
        let parallelSquares = Array.Parallel.map (fun x -> x * x) [| 1..10 |]
        let resize = ResizeArray [ 3; 1; 2 ]
        resize.Sort()
        let counts = Dictionary<string, int>()

        for w in [ "pear"; "fig"; "pear"; "kiwi"; "fig"; "pear" ] do
            counts[w] <-
                match counts.TryGetValue w with
                | true, n -> n + 1
                | _ -> 1

        let queue = Queue<int>([ 1; 2; 3 ])
        let stack = Stack<int>([ 1; 2; 3 ])
        let sorted = SortedDictionary<string, int>(counts)
        let span = System.Span<int>(arr)

        [
            $"sorted = {arr}, as list = {List.ofArray arr}, array ToString = {arr.ToString()}"
            $"array2d = {grid[1, 2]}, lengths = {Array2D.length1 grid}x{Array2D.length2 grid}"
            $"parallel = {List.ofArray parallelSquares}"
            $"slices = {List.ofArray arr[1..2]}, {List.ofArray arr[..1]}, {[ 1..10 ][7..]}"
            $"resize array = {List.ofSeq resize}"
            $"""dictionary = {counts |> Seq.map (fun kv -> $"{kv.Key}={kv.Value}") |> List.ofSeq}"""
            $"sorted dictionary = {sorted.Keys |> List.ofSeq}"
            $"queue = {queue.Dequeue()}, stack = {stack.Pop()}"
            $"span sum = {Seq.sum (span.ToArray())}, slice = {span.Slice(1, 2).Length}"
            $"""dict/readOnlyDict = {(dict [ 1, "a" ])[1]}, {(readOnlyDict [ 2, "b" ])[2]}"""
            $"array functions = {Array.map2 (+) [| 1; 2 |] [| 10; 20 |]}, {Array.fold (+) 0 arr}"
        ])

let scenarios = [ mapsAndSets; lists; sequences; arrays ]
