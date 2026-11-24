module Fx.Contracts.Tests.JsonNumberTests

open System
open Expecto
open FsCheck
open FsCheck.FSharp
open Fx.Contracts
open Fx.Contracts.Tests.Generators

let private parse text =
    match JsonNumber.tryParse text with
    | Some n -> n
    | None -> failtest $"not a JSON number: {text}"

let private normalized text =
    (JsonNumber.normalize (parse text)).Text

/// The same value written in several notations: digits shifted between mantissa and exponent,
/// with extra leading and trailing zeros.
let private notations: Gen<string list> =
    gen {
        let! negative = Gen.elements [ false; true ]
        let! first = Gen.choose (1, 9)
        let! rest = Gen.listOf (Gen.choose (0, 9))
        let digits = string first + String.Join("", rest)
        let! exponent = Gen.choose (-40, 40)
        let sign = if negative then "-" else ""
        let plain = $"{sign}{digits}e{exponent}"
        let shifted = $"{sign}0.{digits}000E{exponent + digits.Length}"
        let padded = $"{sign}{digits}.000e{exponent}"
        let split = $"{sign}{first}.0{digits.Substring 1}e{exponent + digits.Length - 1}"

        let splitValue =
            $"{sign}{first}.{digits.Substring 1}0e{exponent + digits.Length - 1}"

        return
            [ plain; shifted; padded; splitValue ]
            @ (if rest.IsEmpty then [ split ] else [])
    }

[<Tests>]
let tests =
    testList
        "JsonNumber"
        [
            test "grammar" {
                for valid in
                    [
                        "0"
                        "-0"
                        "1"
                        "-1"
                        "10"
                        "1.5"
                        "0.001"
                        "1e5"
                        "1E+5"
                        "1e-005"
                        "-0.0e0"
                    ] do
                    Expect.isSome (JsonNumber.tryParse valid) valid

                for invalid in
                    [
                        ""
                        "-"
                        "+1"
                        "01"
                        "-01"
                        "1."
                        ".5"
                        "1e"
                        "1e+"
                        "0x10"
                        "1 "
                        " 1"
                        "NaN"
                        "Infinity"
                        "1e1234567890"
                        "١"
                    ] do
                    Expect.isNone (JsonNumber.tryParse invalid) invalid
            }

            test "exponents up to 9 significant digits" {
                Expect.isSome (JsonNumber.tryParse "1e999999999") "9 digits"
                Expect.isSome (JsonNumber.tryParse "1e0000000000000999999999") "leading zeros do not count"
                Expect.isNone (JsonNumber.tryParse "1e1000000000") "10 digits"
            }

            test "normalization follows ECMAScript notation, exactly" {
                let cases =
                    [
                        "0", "0"
                        "-0", "0"
                        "-0.000e10", "0"
                        "1.0", "1"
                        "1.50", "1.5"
                        "100", "100"
                        "1e2", "100"
                        "0.1e1", "1"
                        "123.456", "123.456"
                        "1e20", "100000000000000000000"
                        "1e21", "1e+21"
                        "123e19", "1.23e+21"
                        "0.000001", "0.000001"
                        "1e-7", "1e-7"
                        "-12.5e-8", "-1.25e-7"
                        "4.50", "4.5"
                        "2e-3", "0.002"
                        "1E30", "1e+30"
                        "0.000000000000000000000000001", "1e-27"
                        // Exact: no rounding through binary floating point.
                        "333333333.33333329", "333333333.33333329"
                        "9007199254740993", "9007199254740993"
                        "12345678901234567890.123456789", "12345678901234567890.123456789"
                        "1e999999999", "1e+999999999"
                    ]

                for input, expected in cases do
                    Expect.equal (normalized input) expected input
            }

            testPropertyWithConfig config "normalization is idempotent and keeps the value"
            <| fun (n: JsonNumber) ->
                let once = JsonNumber.normalize n
                Expect.equal (JsonNumber.normalize once) once "idempotent"
                Expect.equal (JsonNumber.toFloat once) (JsonNumber.toFloat n) "same nearest float"
                Expect.equal (JsonNumber.tryToInt64 once) (JsonNumber.tryToInt64 n) "same int64"
                Expect.equal (JsonNumber.isInteger once) (JsonNumber.isInteger n) "same integrality"

            testPropertyWithConfig config "equal values normalize to the same text"
            <| Prop.forAll (Arb.fromGen notations) (fun forms ->
                let texts = forms |> List.map normalized |> List.distinct
                Expect.equal texts.Length 1 $"{forms} -> {texts}")

            testPropertyWithConfig config "floats round-trip through their shortest text"
            <| Prop.forAll (Arb.fromGen finiteFloat) (fun f ->
                let n = JsonNumber.ofFloat f
                Expect.equal (JsonNumber.toFloat n) f n.Text
                Expect.equal (JsonNumber.normalize n) n "ofFloat is already canonical")

            test "floats are written as JSON.stringify writes them" {
                let cases =
                    [
                        0.1 + 0.2, "0.30000000000000004"
                        1e21, "1e+21"
                        1e20, "100000000000000000000"
                        1e-7, "1e-7"
                        0.000001, "0.000001"
                        -0.0, "0"
                        123.456, "123.456"
                        5e-324, "5e-324"
                        Double.MaxValue, "1.7976931348623157e+308"
                        -1.5e300, "-1.5e+300"
                        100.0, "100"
                    ]

                for value, expected in cases do
                    Expect.equal (JsonNumber.ofFloat value).Text expected expected
            }

            test "non-finite floats are not JSON numbers" {
                for value in [ nan; infinity; -infinity ] do
                    Expect.throwsT<ArgumentOutOfRangeException> (fun () -> JsonNumber.ofFloat value |> ignore) "throws"
            }

            testPropertyWithConfig config "int64 is exact in any notation"
            <| fun (value: int64) ->
                let text = JsonNumber.ofInt64 value
                Expect.equal (JsonNumber.tryToInt64 text) (Some value) "plain"
                Expect.equal (JsonNumber.tryToInt64 (parse (text.Text + ".000"))) (Some value) "fraction"
                Expect.equal (JsonNumber.tryToInt64 (parse (text.Text + "e0"))) (Some value) "exponent"

                if value <> 0L then
                    Expect.equal (JsonNumber.tryToInt64 (parse (text.Text + "0e-1"))) (Some value) "shifted"

            test "integer conversions check range and integrality" {
                Expect.equal (JsonNumber.tryToInt64 (parse "9223372036854775807")) (Some Int64.MaxValue) "max"
                Expect.equal (JsonNumber.tryToInt64 (parse "-9223372036854775808")) (Some Int64.MinValue) "min"
                Expect.isNone (JsonNumber.tryToInt64 (parse "9223372036854775808")) "max + 1"
                Expect.isNone (JsonNumber.tryToInt64 (parse "-9223372036854775809")) "min - 1"
                Expect.isNone (JsonNumber.tryToInt64 (parse "1e19")) "1e19"
                Expect.equal (JsonNumber.tryToInt64 (parse "9.2e18")) (Some 9200000000000000000L) "9.2e18"
                Expect.isNone (JsonNumber.tryToInt64 (parse "1.5")) "fraction"
                Expect.isNone (JsonNumber.tryToInt64 (parse "1e-400")) "tiny"
                Expect.equal (JsonNumber.tryToInt64 (parse "-0.0")) (Some 0L) "negative zero"
                Expect.equal (JsonNumber.tryToInt (parse "2147483647")) (Some Int32.MaxValue) "int max"
                Expect.isNone (JsonNumber.tryToInt (parse "2147483648")) "int max + 1"
                Expect.isTrue (JsonNumber.isInteger (parse "1.0e3")) "1.0e3"
                Expect.isFalse (JsonNumber.isInteger (parse "1.0001e3")) "1.0001e3"
            }

            testPropertyWithConfig config "decimals keep their scale"
            <| fun (value: decimal) ->
                let n = JsonNumber.ofDecimal value
                let back = JsonNumber.tryToDecimal n
                Expect.equal back (Some value) n.Text

                Expect.equal
                    (back
                     |> Option.map (fun d -> d.ToString(Globalization.CultureInfo.InvariantCulture)))
                    (Some n.Text)
                    "scale"

            test "decimal range" {
                Expect.isNone (JsonNumber.tryToDecimal (parse "1e29")) "too large"
                Expect.equal (JsonNumber.tryToDecimal (parse "1.5e2")) (Some 150M) "exponent"
            }

            test "floats beyond the range become infinities" {
                Expect.equal (JsonNumber.toFloat (parse "1e400")) infinity "large"
                Expect.equal (JsonNumber.toFloat (parse "-1e400")) -infinity "negative"
                Expect.equal (JsonNumber.toFloat (parse "1e-400")) 0.0 "tiny"
            }
        ]
