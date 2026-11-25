/// Encode, decode and preview throughput of codecs (docs/modules/contracts.md, "Performance"): a
/// 4-case union, a 24-case union and a 48-field record, the sizes that T101b made possible under Native
/// AOT. Prints a Markdown table of the median time and allocations per operation. Numbers vary by
/// machine; compare runs on one machine.
module Fx.Contracts.Bench.Program

open System
open System.Diagnostics
open Fx.Contracts

type Span =
    {
        Start: int
        End: int
    }

    override this.ToString() = $"{this.Start}-{this.End}"

let span: Codec<Span> =
    Codec.record {
        let! start = Codec.field "start" (fun s -> s.Start) Codec.int
        and! finish = Codec.field "end" (fun s -> s.End) Codec.int
        return { Start = start; End = finish }
    }
    |> Codec.named "Span"

type Small =
    | Done of summary: string * evidence: Span list * verify: string option
    | Waiting of on: string list * note: string
    | Moved of Span
    | Cancelled

    override this.ToString() =
        match this with
        | Done _ -> "Done"
        | Waiting _ -> "Waiting"
        | Moved _ -> "Moved"
        | Cancelled -> "Cancelled"

let small: Codec<Small> =
    Codec.union "kind" {
        let! done' =
            Codec.case3
                "done"
                (fun summary evidence verify -> Done(summary, evidence, verify))
                (Field.required "summary" Codec.string)
                (Field.withDefault "evidence" [] (Codec.list span))
                (Field.optional "verify" Codec.string)

        and! waiting =
            Codec.case2
                "waiting"
                (fun on note -> Waiting(on, note))
                (Field.required "on" (Codec.list Codec.string))
                (Field.required "note" Codec.string)

        and! moved = Codec.caseOf "moved" Moved span
        and! cancelled = Codec.case0 "cancelled" Cancelled

        return
            fun value ->
                match value with
                | Done(summary, evidence, verify) -> done' summary evidence verify
                | Waiting(on, note) -> waiting on note
                | Moved span -> moved span
                | Cancelled -> cancelled
    }

/// Every shape of case, four times.
type Big =
    | C01
    | C02 of int
    | C03 of string * int
    | C04 of string * int * bool
    | C05 of string * int * bool * float
    | C06 of Span
    | C07
    | C08 of int
    | C09 of string * int
    | C10 of string * int * bool
    | C11 of string * int * bool * float
    | C12 of Span
    | C13
    | C14 of int
    | C15 of string * int
    | C16 of string * int * bool
    | C17 of string * int * bool * float
    | C18 of Span
    | C19
    | C20 of int
    | C21 of string * int
    | C22 of string * int * bool
    | C23 of string * int * bool * float
    | C24 of Span

    override this.ToString() = "Big"

let big: Codec<Big> =
    Codec.union "kind" {
        let! c01 = Codec.case0 "c01" C01
        and! c02 = Codec.case1 "c02" C02 (Field.required "a" Codec.int)

        and! c03 =
            Codec.case2 "c03" (fun a b -> C03(a, b)) (Field.required "a" Codec.string) (Field.required "b" Codec.int)

        and! c04 =
            Codec.case3
                "c04"
                (fun a b c -> C04(a, b, c))
                (Field.required "a" Codec.string)
                (Field.required "b" Codec.int)
                (Field.required "c" Codec.bool)

        and! c05 =
            Codec.case4
                "c05"
                (fun a b c d -> C05(a, b, c, d))
                (Field.required "a" Codec.string)
                (Field.required "b" Codec.int)
                (Field.required "c" Codec.bool)
                (Field.required "d" Codec.float)

        and! c06 = Codec.caseOf "c06" C06 span
        and! c07 = Codec.case0 "c07" C07
        and! c08 = Codec.case1 "c08" C08 (Field.required "a" Codec.int)

        and! c09 =
            Codec.case2 "c09" (fun a b -> C09(a, b)) (Field.required "a" Codec.string) (Field.required "b" Codec.int)

        and! c10 =
            Codec.case3
                "c10"
                (fun a b c -> C10(a, b, c))
                (Field.required "a" Codec.string)
                (Field.required "b" Codec.int)
                (Field.required "c" Codec.bool)

        and! c11 =
            Codec.case4
                "c11"
                (fun a b c d -> C11(a, b, c, d))
                (Field.required "a" Codec.string)
                (Field.required "b" Codec.int)
                (Field.required "c" Codec.bool)
                (Field.required "d" Codec.float)

        and! c12 = Codec.caseOf "c12" C12 span
        and! c13 = Codec.case0 "c13" C13
        and! c14 = Codec.case1 "c14" C14 (Field.required "a" Codec.int)

        and! c15 =
            Codec.case2 "c15" (fun a b -> C15(a, b)) (Field.required "a" Codec.string) (Field.required "b" Codec.int)

        and! c16 =
            Codec.case3
                "c16"
                (fun a b c -> C16(a, b, c))
                (Field.required "a" Codec.string)
                (Field.required "b" Codec.int)
                (Field.required "c" Codec.bool)

        and! c17 =
            Codec.case4
                "c17"
                (fun a b c d -> C17(a, b, c, d))
                (Field.required "a" Codec.string)
                (Field.required "b" Codec.int)
                (Field.required "c" Codec.bool)
                (Field.required "d" Codec.float)

        and! c18 = Codec.caseOf "c18" C18 span
        and! c19 = Codec.case0 "c19" C19
        and! c20 = Codec.case1 "c20" C20 (Field.required "a" Codec.int)

        and! c21 =
            Codec.case2 "c21" (fun a b -> C21(a, b)) (Field.required "a" Codec.string) (Field.required "b" Codec.int)

        and! c22 =
            Codec.case3
                "c22"
                (fun a b c -> C22(a, b, c))
                (Field.required "a" Codec.string)
                (Field.required "b" Codec.int)
                (Field.required "c" Codec.bool)

        and! c23 =
            Codec.case4
                "c23"
                (fun a b c d -> C23(a, b, c, d))
                (Field.required "a" Codec.string)
                (Field.required "b" Codec.int)
                (Field.required "c" Codec.bool)
                (Field.required "d" Codec.float)

        and! c24 = Codec.caseOf "c24" C24 span

        return
            fun value ->
                match value with
                | C01 -> c01
                | C02 a -> c02 a
                | C03(a, b) -> c03 a b
                | C04(a, b, c) -> c04 a b c
                | C05(a, b, c, d) -> c05 a b c d
                | C06 a -> c06 a
                | C07 -> c07
                | C08 a -> c08 a
                | C09(a, b) -> c09 a b
                | C10(a, b, c) -> c10 a b c
                | C11(a, b, c, d) -> c11 a b c d
                | C12 a -> c12 a
                | C13 -> c13
                | C14 a -> c14 a
                | C15(a, b) -> c15 a b
                | C16(a, b, c) -> c16 a b c
                | C17(a, b, c, d) -> c17 a b c d
                | C18 a -> c18 a
                | C19 -> c19
                | C20 a -> c20 a
                | C21(a, b) -> c21 a b
                | C22(a, b, c) -> c22 a b c
                | C23(a, b, c, d) -> c23 a b c d
                | C24 a -> c24 a
    }

/// 48 fields, six kinds of field eight times.
type Wide =
    {
        F01: string
        F02: int
        F03: float
        F04: bool
        F05: string list
        F06: string option
        F07: string
        F08: int
        F09: float
        F10: bool
        F11: string list
        F12: string option
        F13: string
        F14: int
        F15: float
        F16: bool
        F17: string list
        F18: string option
        F19: string
        F20: int
        F21: float
        F22: bool
        F23: string list
        F24: string option
        F25: string
        F26: int
        F27: float
        F28: bool
        F29: string list
        F30: string option
        F31: string
        F32: int
        F33: float
        F34: bool
        F35: string list
        F36: string option
        F37: string
        F38: int
        F39: float
        F40: bool
        F41: string list
        F42: string option
        F43: string
        F44: int
        F45: float
        F46: bool
        F47: string list
        F48: string option
    }

    override this.ToString() = "Wide"

let wide: Codec<Wide> =
    Codec.record {
        let! f01 = Codec.field "f01" (fun w -> w.F01) Codec.string
        and! f02 = Codec.field "f02" (fun w -> w.F02) Codec.int
        and! f03 = Codec.field "f03" (fun w -> w.F03) Codec.float
        and! f04 = Codec.field "f04" (fun w -> w.F04) Codec.bool
        and! f05 = Codec.defaultField "f05" (fun w -> w.F05) [] (Codec.list Codec.string)
        and! f06 = Codec.optionalField "f06" (fun w -> w.F06) Codec.string
        and! f07 = Codec.field "f07" (fun w -> w.F07) Codec.string
        and! f08 = Codec.field "f08" (fun w -> w.F08) Codec.int
        and! f09 = Codec.field "f09" (fun w -> w.F09) Codec.float
        and! f10 = Codec.field "f10" (fun w -> w.F10) Codec.bool
        and! f11 = Codec.defaultField "f11" (fun w -> w.F11) [] (Codec.list Codec.string)
        and! f12 = Codec.optionalField "f12" (fun w -> w.F12) Codec.string
        and! f13 = Codec.field "f13" (fun w -> w.F13) Codec.string
        and! f14 = Codec.field "f14" (fun w -> w.F14) Codec.int
        and! f15 = Codec.field "f15" (fun w -> w.F15) Codec.float
        and! f16 = Codec.field "f16" (fun w -> w.F16) Codec.bool
        and! f17 = Codec.defaultField "f17" (fun w -> w.F17) [] (Codec.list Codec.string)
        and! f18 = Codec.optionalField "f18" (fun w -> w.F18) Codec.string
        and! f19 = Codec.field "f19" (fun w -> w.F19) Codec.string
        and! f20 = Codec.field "f20" (fun w -> w.F20) Codec.int
        and! f21 = Codec.field "f21" (fun w -> w.F21) Codec.float
        and! f22 = Codec.field "f22" (fun w -> w.F22) Codec.bool
        and! f23 = Codec.defaultField "f23" (fun w -> w.F23) [] (Codec.list Codec.string)
        and! f24 = Codec.optionalField "f24" (fun w -> w.F24) Codec.string
        and! f25 = Codec.field "f25" (fun w -> w.F25) Codec.string
        and! f26 = Codec.field "f26" (fun w -> w.F26) Codec.int
        and! f27 = Codec.field "f27" (fun w -> w.F27) Codec.float
        and! f28 = Codec.field "f28" (fun w -> w.F28) Codec.bool
        and! f29 = Codec.defaultField "f29" (fun w -> w.F29) [] (Codec.list Codec.string)
        and! f30 = Codec.optionalField "f30" (fun w -> w.F30) Codec.string
        and! f31 = Codec.field "f31" (fun w -> w.F31) Codec.string
        and! f32 = Codec.field "f32" (fun w -> w.F32) Codec.int
        and! f33 = Codec.field "f33" (fun w -> w.F33) Codec.float
        and! f34 = Codec.field "f34" (fun w -> w.F34) Codec.bool
        and! f35 = Codec.defaultField "f35" (fun w -> w.F35) [] (Codec.list Codec.string)
        and! f36 = Codec.optionalField "f36" (fun w -> w.F36) Codec.string
        and! f37 = Codec.field "f37" (fun w -> w.F37) Codec.string
        and! f38 = Codec.field "f38" (fun w -> w.F38) Codec.int
        and! f39 = Codec.field "f39" (fun w -> w.F39) Codec.float
        and! f40 = Codec.field "f40" (fun w -> w.F40) Codec.bool
        and! f41 = Codec.defaultField "f41" (fun w -> w.F41) [] (Codec.list Codec.string)
        and! f42 = Codec.optionalField "f42" (fun w -> w.F42) Codec.string
        and! f43 = Codec.field "f43" (fun w -> w.F43) Codec.string
        and! f44 = Codec.field "f44" (fun w -> w.F44) Codec.int
        and! f45 = Codec.field "f45" (fun w -> w.F45) Codec.float
        and! f46 = Codec.field "f46" (fun w -> w.F46) Codec.bool
        and! f47 = Codec.defaultField "f47" (fun w -> w.F47) [] (Codec.list Codec.string)
        and! f48 = Codec.optionalField "f48" (fun w -> w.F48) Codec.string

        return
            {
                F01 = f01
                F02 = f02
                F03 = f03
                F04 = f04
                F05 = f05
                F06 = f06
                F07 = f07
                F08 = f08
                F09 = f09
                F10 = f10
                F11 = f11
                F12 = f12
                F13 = f13
                F14 = f14
                F15 = f15
                F16 = f16
                F17 = f17
                F18 = f18
                F19 = f19
                F20 = f20
                F21 = f21
                F22 = f22
                F23 = f23
                F24 = f24
                F25 = f25
                F26 = f26
                F27 = f27
                F28 = f28
                F29 = f29
                F30 = f30
                F31 = f31
                F32 = f32
                F33 = f33
                F34 = f34
                F35 = f35
                F36 = f36
                F37 = f37
                F38 = f38
                F39 = f39
                F40 = f40
                F41 = f41
                F42 = f42
                F43 = f43
                F44 = f44
                F45 = f45
                F46 = f46
                F47 = f47
                F48 = f48
            }
    }

let sampleWide =
    {
        F01 = "s1"
        F02 = 2
        F03 = 3.25
        F04 = true
        F05 = [ "a"; "b" ]
        F06 = Some "o6"
        F07 = "s7"
        F08 = 8
        F09 = 9.25
        F10 = true
        F11 = [ "a"; "b" ]
        F12 = Some "o12"
        F13 = "s13"
        F14 = 14
        F15 = 15.25
        F16 = true
        F17 = [ "a"; "b" ]
        F18 = Some "o18"
        F19 = "s19"
        F20 = 20
        F21 = 21.25
        F22 = true
        F23 = [ "a"; "b" ]
        F24 = Some "o24"
        F25 = "s25"
        F26 = 26
        F27 = 27.25
        F28 = true
        F29 = [ "a"; "b" ]
        F30 = Some "o30"
        F31 = "s31"
        F32 = 32
        F33 = 33.25
        F34 = true
        F35 = [ "a"; "b" ]
        F36 = Some "o36"
        F37 = "s37"
        F38 = 38
        F39 = 39.25
        F40 = true
        F41 = [ "a"; "b" ]
        F42 = Some "o42"
        F43 = "s43"
        F44 = 44
        F45 = 45.25
        F46 = true
        F47 = [ "a"; "b" ]
        F48 = Some "o48"
    }

let sampleSmall =
    [
        Done("all green", [ { Start = 1; End = 4 }; { Start = 7; End = 9 } ], Some "dotnet test")
        Waiting([ "build"; "lint" ], "after the build")
        Moved { Start = 3; End = 5 }
        Cancelled
    ]

let sampleBig =
    [
        C01
        C02 2
        C03("b3", 3)
        C04("c4", 4, true)
        C05("d5", 5, false, 1.5)
        C06 { Start = 6; End = 8 }
        C07
        C08 8
        C09("b9", 9)
        C10("c10", 10, true)
        C11("d11", 11, false, 1.5)
        C12 { Start = 12; End = 14 }
        C13
        C14 14
        C15("b15", 15)
        C16("c16", 16, true)
        C17("d17", 17, false, 1.5)
        C18 { Start = 18; End = 20 }
        C19
        C20 20
        C21("b21", 21)
        C22("c22", 22, true)
        C23("d23", 23, false, 1.5)
        C24 { Start = 24; End = 26 }
    ]

/// Median nanoseconds and bytes allocated per call of `run`, which does `operations` operations.
let measure (iterations: int) (operations: int) (run: unit -> unit) : float * float =
    for _ in 1 .. iterations / 10 do
        run ()

    let samples =
        [
            for _ in 1..7 ->
                GC.Collect()
                let bytes = GC.GetTotalAllocatedBytes true
                let start = Stopwatch.GetTimestamp()

                for _ in 1..iterations do
                    run ()

                let elapsed = Stopwatch.GetElapsedTime start
                let allocated = GC.GetTotalAllocatedBytes true - bytes
                let count = float (iterations * operations)
                elapsed.TotalNanoseconds / count, float allocated / count
        ]

    let median (values: float list) =
        List.sort values |> List.item (values.Length / 2)

    median (List.map fst samples), median (List.map snd samples)

/// Encoding, decoding and previewing every value, each value once per operation.
let operations (codec: Codec<'T>) (values: 'T list) : (string * (unit -> unit)) list =
    let encoded = values |> List.map codec.Encode

    let check =
        encoded
        |> List.forall (fun json ->
            match codec.Decode json with
            | Ok value -> codec.Encode value = json
            | Error _ -> false)

    if not check then
        failwith "The benchmark values do not round-trip."

    [
        "encode",
        fun () ->
            for value in values do
                codec.Encode value |> ignore
        "decode",
        fun () ->
            for json in encoded do
                codec.Decode json |> ignore
        "preview",
        fun () ->
            for value in values do
                codec.Preview Limits.defaults value |> ignore
    ]

[<EntryPoint>]
let main _ =
    let cases =
        [
            "union, 4 cases", 200_000, sampleSmall.Length, operations small sampleSmall
            "union, 24 cases", 50_000, sampleBig.Length, operations big sampleBig
            "record, 48 fields", 20_000, 1, operations wide [ sampleWide ]
        ]

    Console.WriteLine
        $".NET {Environment.Version}, {Environment.ProcessorCount} cores, {Runtime.InteropServices.RuntimeInformation.OSDescription}"

    Console.WriteLine ""
    Console.WriteLine "| Codec | Operation | Time per value | Allocated per value |"
    Console.WriteLine "|---|---|---|---|"

    for name, iterations, count, operations in cases do
        for operation, run in operations do
            let nanoseconds, bytes = measure iterations count run
            Console.WriteLine $"| {name} | {operation} | {nanoseconds:F0} ns | {bytes:F0} B |"

    0
