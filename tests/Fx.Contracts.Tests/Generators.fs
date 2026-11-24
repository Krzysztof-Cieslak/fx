/// FsCheck generators for JSON values, number texts, strings and preview limits.
module Fx.Contracts.Tests.Generators

open System
open FsCheck
open FsCheck.FSharp
open Expecto
open Fx.Contracts

let private digits (minLength: int) (maxLength: int) : Gen<string> =
    gen {
        let! length = Gen.choose (minLength, maxLength)
        let! ds = Gen.listOfLength length (Gen.choose (0, 9))
        return String.Join("", ds)
    }

/// Number texts that follow the JSON grammar, with long digit runs and exponents.
let numberText: Gen<string> =
    gen {
        let! negative = Gen.elements [ false; true ]

        let! integer =
            Gen.frequency
                [
                    1, Gen.constant "0"
                    4,
                    gen {
                        let! first = Gen.choose (1, 9)
                        let! rest = digits 0 25
                        return string first + rest
                    }
                ]

        let! fraction =
            Gen.frequency
                [
                    2, Gen.constant ""
                    1,
                    gen {
                        let! ds = digits 1 25
                        return "." + ds
                    }
                ]

        let! exponent =
            Gen.frequency
                [
                    2, Gen.constant ""
                    1,
                    gen {
                        let! e = Gen.elements [ "e"; "E" ]
                        let! sign = Gen.elements [ ""; "+"; "-" ]
                        let! ds = digits 1 4
                        return e + sign + ds
                    }
                ]

        return (if negative then "-" else "") + integer + fraction + exponent
    }

let finiteFloat: Gen<float> =
    ArbMap.defaults |> ArbMap.generate<float> |> Gen.filter Double.IsFinite

let number: Gen<JsonNumber> =
    Gen.oneof
        [
            ArbMap.defaults |> ArbMap.generate<int64> |> Gen.map JsonNumber.ofInt64
            finiteFloat |> Gen.map JsonNumber.ofFloat
            ArbMap.defaults |> ArbMap.generate<decimal> |> Gen.map JsonNumber.ofDecimal
            numberText |> Gen.map (JsonNumber.tryParse >> Option.get)
        ]

let private samples =
    [
        ""
        "a"
        "hello world"
        "Zażółć gęślą jaźń"
        "😀 emoji"
        "\"quoted\""
        "back\\slash"
        "line\nbreak\r\n"
        "tab\tand\u0000nul\u001f"
        "\u007f\u0080\u2028\u2029"
        "/slash/"
        "key with spaces"
        "$dollar_ok"
    ]

// F# string literals cannot hold lone surrogates (they become U+FFFD): build them from code units.
let private surrogates =
    [
        "lone " + string (char 0xD800) + " high"
        "lone " + string (char 0xDC00) + " low"
        string (char 0xDE00) + string (char 0xD83D) + " reversed pair"
    ]

/// Strings, never null: samples, arbitrary strings and arbitrary UTF-16 code units (lone surrogates
/// included).
let text: Gen<string> =
    Gen.frequency
        [
            3, Gen.elements (samples @ surrogates)
            4,
            ArbMap.defaults
            |> ArbMap.generate<string>
            |> Gen.filter (fun s -> not (obj.ReferenceEquals(s, null)))
            2, Gen.arrayOf (Gen.choose (0, 0xFFFF) |> Gen.map char) |> Gen.map String
        ]

let private scalar: Gen<Json> =
    Gen.frequency
        [
            1, Gen.constant Json.Null
            1, Gen.elements [ Json.Bool true; Json.Bool false ]
            3, number |> Gen.map Json.Number
            3, text |> Gen.map Json.String
        ]

let rec private jsonOfSize (size: int) : Gen<Json> =
    if size <= 1 then
        scalar
    else
        let child = jsonOfSize (size / 3)

        Gen.frequency
            [
                3, scalar
                1, Gen.listOf child |> Gen.map Json.Array
                1,
                Gen.listOf (Gen.zip text child)
                |> Gen.map (fun members -> Json.Object(List.distinctBy fst members))
            ]

let json: Gen<Json> = Gen.sized jsonOfSize

/// Limits from tiny to generous, negative values included.
let limits: Gen<Limits> =
    gen {
        let! maxChars =
            Gen.frequency [ 1, Gen.choose (-2, 4); 3, Gen.choose (5, 80); 1, Gen.choose (80, 2000) ]

        let! maxItems = Gen.choose (-1, 12)
        let! maxDepth = Gen.choose (-1, 5)
        let! maxStringChars = Gen.choose (-1, 60)

        return
            {
                MaxChars = maxChars
                MaxItems = maxItems
                MaxDepth = maxDepth
                MaxStringChars = maxStringChars
            }
    }

/// Registered with FsCheck through `config`.
type Arbitraries =
    static member Json() : Arbitrary<Json> = Arb.fromGen json
    static member JsonNumber() : Arbitrary<JsonNumber> = Arb.fromGen number
    static member String() : Arbitrary<string> = Arb.fromGen text
    static member Limits() : Arbitrary<Limits> = Arb.fromGen limits

/// 300 tests per property; set PROPERTY_TESTS to run more.
let config =
    { FsCheckConfig.defaultConfig with
        arbitrary = [ typeof<Arbitraries> ]
        maxTest =
            match Environment.GetEnvironmentVariable "PROPERTY_TESTS" with
            | null -> 300
            | count -> int count
    }

/// A property with the generators above.
let property name (test: 'T -> unit) = testPropertyWithConfig config name test

/// Whether every surrogate in the text is part of a pair.
let isWellFormed (text: string) : bool =
    let mutable ok = true
    let mutable i = 0

    while ok && i < text.Length do
        let c = text[i]

        if Char.IsHighSurrogate c then
            if i + 1 < text.Length && Char.IsLowSurrogate text[i + 1] then
                i <- i + 2
            else
                ok <- false
        elif Char.IsLowSurrogate c then
            ok <- false
        else
            i <- i + 1

    ok
