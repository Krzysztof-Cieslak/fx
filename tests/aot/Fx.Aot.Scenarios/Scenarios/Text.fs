/// Strings: interpolation without format specifiers, number formatting, Unicode, encodings and regex.
module Fx.Aot.Scenarios.Text

open System
open System.Globalization
open System.Text
open System.Text.RegularExpressions
open Fx.Aot.Scenarios.Show

let interpolation =
    Scenario.lines "string-interpolation" (fun () ->
        let nullString: string | null = null

        [
            $"ints = {42} {-7L} {255uy} {65535us} {4294967295u} {-128y} {9223372036854775807L}"
            $"floats = {1.0} {1.5} {0.1 + 0.2} {1e21} {1e-7} {-0.0} {Math.PI} {nan} {infinity} {-infinity}"
            $"float32 = {1.5f} {0.1f} {3.4028235e38f}"
            $"decimals = {1.10M} {0.1M + 0.2M} {Decimal.MaxValue} {-0.5M}"
            $"bigint = {2I ** 100} {-(3I ** 40)}"
            $"chars and bools = {'x'} {true} {false}"
            $"null string = [{nullString}]"
            $"""nested = {$"inner {1 + 1}"}"""
            $"""triple quoted = {"quoted"} {[ "a"; "b" ]}"""
            $"multiline = {String.Join('|', [ 1; 2; 3 ])}"
        ])

let numbers =
    Scenario.lines "number-formatting" (fun () ->
        let inv = CultureInfo.InvariantCulture
        let date = DateTime(2024, 2, 29, 13, 45, 30, 123, DateTimeKind.Utc)
        let offset = DateTimeOffset(date).ToOffset(TimeSpan.FromHours 5.5)

        [
            $"""standard = {(1234.5678).ToString("N2", inv)} {(255).ToString("X4", inv)} {(0.125).ToString("P1", inv)}"""
            $"""round trip = {(0.1).ToString("R", inv)} {(1.0 / 3.0).ToString(inv)} {Double.Parse("2.5e-3", inv)}"""
            $"""composite = {String.Format(inv, "{0,6}|{1,-6}|{2:0.00}", 42, "ab", 3.14159)}"""
            $"""parse = {Int32.Parse "-17"} {Int64.Parse "123456789012"} {Decimal.Parse("1.25", inv)}"""
            $"""try parse = {Int32.TryParse "12x"} {Double.TryParse("1.5", NumberStyles.Float, inv)}"""
            $"conversions = {int 3.99} {int -3.99} {round 2.5} {round 3.5} {floor -1.5} {Math.Round(2.345, 2)}"
            $"overflow wraps = {Int32.MaxValue + 1} {byte 300}"
            $"""date = {date.ToString("o", inv)} {date.DayOfWeek} {date.DayOfYear}"""
            $"""offset = {offset.ToString("yyyy-MM-dd HH:mm:ss zzz", inv)}"""
            $"timespan = {TimeSpan.FromMinutes 90.5} {TimeSpan(1, 2, 3, 4, 5)}"
            $"""guid = {Guid.Parse "6f9619ff-8b86-d011-b42d-00c04fc964ff"} {Guid.Empty.ToString "N"}"""
            $"""enum = {DayOfWeek.Wednesday} {int DayOfWeek.Wednesday} {Enum.Parse<DayOfWeek> "Friday"} {StringComparison.OrdinalIgnoreCase}"""
            $"flags enum = {(IO.FileAttributes.ReadOnly ||| IO.FileAttributes.Hidden)}"
        ])

let strings =
    Scenario.lines "strings-and-unicode" (fun () ->
        let text = "Zażółć gęślą jaźń 😀 €"
        let utf8 = Encoding.UTF8.GetBytes text
        let sb = StringBuilder()

        for i in 1..5 do
            sb.Append(i).Append(',') |> ignore

        [
            kv "text" text
            $"length = {text.Length}, runes = {text.EnumerateRunes() |> Seq.length}, utf8 bytes = {utf8.Length}"
            $"utf8 hex = {Convert.ToHexString(utf8, 0, 12)}"
            kv "upper invariant" (text.ToUpperInvariant())
            kv "round trip" (Encoding.UTF8.GetString utf8)
            $"""utf16 = {Encoding.Unicode.GetByteCount text}, ascii = {Encoding.ASCII.GetString(Encoding.ASCII.GetBytes "a\u00e9b")}"""
            $"base64 = {Convert.ToBase64String utf8}"
            $"""split = {"a,b,,c".Split(',')}, {List.ofArray ("a,b,,c".Split(',', StringSplitOptions.RemoveEmptyEntries))}"""
            $"""trim/pad = [{"  x  ".Trim()}] [{"7".PadLeft(3, '0')}] [{"ab".PadRight 4}]"""
            $"""replace = {"hello world".Replace("o", "0")}, insert = {"abc".Insert(1, "-")}"""
            $"""slicing = {"abcdef"[1..3]} {"abcdef"[..1]} {"abcdef"[4..]}"""
            $"""String module = {String.replicate 3 "ab"} {String.map Char.ToUpperInvariant "abc"} {String.concat "/" [ "x"; "y" ]}"""
            $"""ordinal compare = {String.CompareOrdinal("a", "B")} {String.Equals("A", "a", StringComparison.OrdinalIgnoreCase)}"""
            $"""contains = {"haystack".Contains "st"} {"haystack".IndexOf 'y'} {"haystack".StartsWith "hay"}"""
            $"string builder = {sb.ToString().TrimEnd(',')}"
            kv "char ops" (String [| Char.ToUpperInvariant 'ż'; Char.ToLowerInvariant 'Ä'; char 0x41 |])
            $"char classes = {Char.IsLetter 'ł'} {Char.IsDigit '٣'} {Char.IsWhiteSpace '\u00a0'} {Char.IsSurrogate(text, 18)}"
        ])

let regex =
    Scenario.lines "regex" (fun () ->
        let input = "id=42; name=alpha; id=7; name=Beta"
        let pairs = Regex(@"(?<key>\w+)=(?<value>\w+)")
        let compiled = Regex(@"\d+", RegexOptions.Compiled)

        [
            yield!
                pairs.Matches input
                |> Seq.map (fun m -> $"""{m.Groups["key"].Value} -> {m.Groups["value"].Value}""")
            $"""replace = {compiled.Replace(input, "#")}"""
            $"evaluator = {compiled.Replace(input, MatchEvaluator(fun m -> string (int m.Value * 2)))}"
            $"""split = {Regex.Split("a1b22c333d", @"\d+") |> List.ofArray}"""
            $"""ignore case = {Regex.IsMatch("BETA", "beta", RegexOptions.IgnoreCase)}"""
            $"""non-backtracking = {Regex.Match("aaab", "a+b", RegexOptions.NonBacktracking).Value}"""
            $"""escape = {Regex.Escape "a.b*c"}"""
        ])

let scenarios = [ interpolation; numbers; strings; regex ]
