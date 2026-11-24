namespace Fx.Contracts

open System
open System.Globalization
open System.Text

/// A JSON number, kept as its exact decimal text so that no precision is lost (int64 and decimal values
/// survive unchanged). The text always follows the RFC 8259 grammar
/// `-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?`, with at most 9 significant exponent digits.
///
/// Equality compares the text: `1.0` and `1` are different `JsonNumber`s. Compare
/// `JsonNumber.normalize` results to compare values.
[<NoComparison>]
type JsonNumber =
    private
    | Raw of string

    /// The exact number text.
    member this.Text =
        let (Raw text) = this
        text

    override this.ToString() = this.Text

/// Constructors, conversions and the canonical normalization of `JsonNumber`.
///
/// Conversions from text go through .NET's invariant-culture parsers; they are the only
/// platform-specific code in the JSON layer.
[<RequireQualifiedAccess>]
module JsonNumber =

    let inline private isDigit (c: char) = c >= '0' && c <= '9'

    /// Exponents with more significant digits than this are rejected (RFC 8259 §9 allows limits).
    [<Literal>]
    let internal MaxExponentDigits = 9

    /// Scans a number starting at `start`. Returns the end offset, or the offset and message of the
    /// first error. Shared by the reader and `tryParse`, so both accept exactly the same texts.
    let internal scan (text: string) (start: int) : Result<int, int * string> =
        let length = text.Length
        let mutable i = start
        let mutable error = None
        let fail offset (message: string) = error <- Some(offset, message)

        if i < length && text[i] = '-' then
            i <- i + 1

        if i >= length || not (isDigit text[i]) then
            fail i "expected a digit"
        elif text[i] = '0' then
            i <- i + 1

            if i < length && isDigit text[i] then
                fail i "leading zeros are not allowed in numbers"
        else
            while i < length && isDigit text[i] do
                i <- i + 1

        if error.IsNone && i < length && text[i] = '.' then
            i <- i + 1

            if i >= length || not (isDigit text[i]) then
                fail i "expected a digit after the decimal point"
            else
                while i < length && isDigit text[i] do
                    i <- i + 1

        if error.IsNone && i < length && (text[i] = 'e' || text[i] = 'E') then
            i <- i + 1

            if i < length && (text[i] = '+' || text[i] = '-') then
                i <- i + 1

            if i >= length || not (isDigit text[i]) then
                fail i "expected a digit in the exponent"
            else
                let digitsStart = i

                while i < length && text[i] = '0' do
                    i <- i + 1

                let significantStart = i

                while i < length && isDigit text[i] do
                    i <- i + 1

                if i - significantStart > MaxExponentDigits then
                    fail digitsStart "number exponent is too large"

        match error with
        | Some e -> Error e
        | None -> Ok i

    /// Wraps text that `scan` accepted in full.
    let internal ofValidText (text: string) : JsonNumber = Raw text

    /// Parses exact number text; `None` unless the whole text follows the JSON number grammar.
    let tryParse (text: string) : JsonNumber option =
        match scan text 0 with
        | Ok finish when finish = text.Length -> Some(Raw text)
        | _ -> None

    /// The value as sign, significant digits and decimal exponent: `0.DIGITS × 10^point`.
    /// `digits` has no leading or trailing zeros and is empty for zero.
    [<Struct>]
    type private Decimal =
        {
            Negative: bool
            Digits: string
            Point: int64
        }

    let private decompose (number: JsonNumber) : Decimal =
        let text = number.Text
        let negative = text[0] = '-'
        let mutable i = if negative then 1 else 0
        let intStart = i

        while i < text.Length && isDigit text[i] do
            i <- i + 1

        let intDigits = text.Substring(intStart, i - intStart)
        let mutable fracDigits = ""

        if i < text.Length && text[i] = '.' then
            let fracStart = i + 1
            i <- fracStart

            while i < text.Length && isDigit text[i] do
                i <- i + 1

            fracDigits <- text.Substring(fracStart, i - fracStart)

        let mutable exponent = 0L

        if i < text.Length then
            // 'e' or 'E', then an optional sign and at most MaxExponentDigits significant digits.
            i <- i + 1
            let expNegative = text[i] = '-'

            if text[i] = '+' || text[i] = '-' then
                i <- i + 1

            while i < text.Length do
                exponent <- exponent * 10L + int64 (int text[i] - int '0')
                i <- i + 1

            if expNegative then
                exponent <- -exponent

        let all = intDigits + fracDigits
        let digits = all.TrimStart('0').TrimEnd('0')

        if digits.Length = 0 then
            {
                Negative = false
                Digits = ""
                Point = 0L
            }
        else
            let leadingZeros = all.Length - all.TrimStart('0').Length
            // all = 0…0 <digits> 0…0 × 10^(exponent - fracDigits.Length); the point sits after the
            // leading zeros and digits.
            let point = exponent - int64 fracDigits.Length + int64 (all.Length - leadingZeros)

            {
                Negative = negative
                Digits = digits
                Point = point
            }

    /// Renders a decomposed value the way ECMAScript's `Number.prototype.toString` places the point,
    /// but over exact decimal digits.
    let private render (d: Decimal) : string =
        if d.Digits.Length = 0 then
            "0"
        else
            let sb = StringBuilder()

            if d.Negative then
                sb.Append '-' |> ignore

            let k = int64 d.Digits.Length
            let n = d.Point

            if k <= n && n <= 21L then
                sb.Append(d.Digits).Append('0', int (n - k)) |> ignore
            elif 0L < n && n <= 21L then
                sb.Append(d.Digits, 0, int n).Append('.').Append(d.Digits, int n, int (k - n))
                |> ignore
            elif -6L < n && n <= 0L then
                sb.Append("0.").Append('0', int -n).Append(d.Digits) |> ignore
            else
                let e = n - 1L
                sb.Append d.Digits[0] |> ignore

                if k > 1L then
                    sb.Append('.').Append(d.Digits, 1, int (k - 1L)) |> ignore

                sb.Append('e').Append(if e > 0L then '+' else '-').Append(string (abs e))
                |> ignore

            sb.ToString()

    /// The canonical text of the same value: no leading or trailing zeros, `-0` becomes `0`, and the
    /// notation follows ECMAScript's thresholds (`1e+21`, `1e-7`, `0.000001`, `123.45`). Exact: it never
    /// goes through binary floating point.
    let normalize (number: JsonNumber) : JsonNumber = Raw(render (decompose number))

    let ofInt (value: int) : JsonNumber =
        Raw(value.ToString(CultureInfo.InvariantCulture))

    let ofInt64 (value: int64) : JsonNumber =
        Raw(value.ToString(CultureInfo.InvariantCulture))

    /// The decimal's exact text, including its scale (`1.10` stays `1.10`).
    let ofDecimal (value: decimal) : JsonNumber =
        Raw(value.ToString(CultureInfo.InvariantCulture))

    /// The shortest text that round-trips to the same float, in canonical notation (as JavaScript's
    /// `JSON.stringify`). Throws for NaN and infinities, which JSON cannot represent.
    let ofFloat (value: float) : JsonNumber =
        if Double.IsFinite value then
            normalize (Raw(value.ToString("R", CultureInfo.InvariantCulture)))
        else
            raise (ArgumentOutOfRangeException(nameof value, "JSON numbers must be finite."))

    /// The nearest float. Values beyond the float range become infinities.
    let toFloat (number: JsonNumber) : float =
        Double.Parse(number.Text, NumberStyles.Float, CultureInfo.InvariantCulture)

    /// The nearest decimal, or `None` beyond the decimal range. Digits beyond decimal precision round.
    let tryToDecimal (number: JsonNumber) : decimal option =
        match Decimal.TryParse(number.Text, NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, value -> Some value
        | _ -> None

    /// Whether the value is a whole number (`3`, `3.0` and `3e2` are; `3.5` is not).
    let isInteger (number: JsonNumber) : bool =
        let d = decompose number
        d.Point >= int64 d.Digits.Length

    /// The exact int64 value, or `None` if the value is not a whole number in the int64 range.
    let tryToInt64 (number: JsonNumber) : int64 option =
        let d = decompose number
        let k = int64 d.Digits.Length

        if d.Point < k || d.Point > 19L then
            if k = 0L then Some 0L else None
        else
            // Accumulate the magnitude negatively: the int64 range is asymmetric.
            let mutable value = 0L
            let mutable overflow = false

            for i in 0 .. int d.Point - 1 do
                let digit =
                    if int64 i < k then
                        int64 (int d.Digits[i] - int '0')
                    else
                        0L

                if not overflow then
                    if value < (Int64.MinValue + digit) / 10L then
                        overflow <- true
                    else
                        value <- value * 10L - digit

            if overflow then None
            elif d.Negative then Some value
            elif value = Int64.MinValue then None
            else Some -value

    /// The exact int value, or `None` if the value is not a whole number in the int range.
    let tryToInt (number: JsonNumber) : int option =
        match tryToInt64 number with
        | Some value when value >= int64 Int32.MinValue && value <= int64 Int32.MaxValue -> Some(int value)
        | _ -> None
