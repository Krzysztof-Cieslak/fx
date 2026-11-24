namespace Fx.Contracts

open System
open System.Text

/// Bounds for a preview (spec §6.6–6.7). Their values are variants. Negative values count as zero.
type Limits =
    {
        /// Maximum length of the whole preview text in UTF-16 code units. Never exceeded.
        MaxChars: int
        /// Maximum number of elements shown per array, list or map.
        MaxItems: int
        /// Maximum nesting of containers shown; deeper ones are summarized, e.g. `Match[](len=37)`.
        MaxDepth: int
        /// Maximum number of characters shown per string.
        MaxStringChars: int
    }

    override this.ToString() =
        $"Limits(MaxChars={this.MaxChars}, MaxItems={this.MaxItems}, MaxDepth={this.MaxDepth}, MaxStringChars={this.MaxStringChars})"

[<RequireQualifiedAccess>]
module Limits =
    /// 1000 characters, 10 items per collection, 3 levels of nesting, 120 characters per string.
    let defaults =
        {
            MaxChars = 1000
            MaxItems = 10
            MaxDepth = 3
            MaxStringChars = 120
        }

/// A bounded, model-readable rendering of a value: the model sees previews and passes values by name
/// (spec §6.6). `Text` never exceeds the `Limits.MaxChars` it was rendered for. `Truncated` means
/// something was left out; the text marks each omission with `…`.
type Preview =
    {
        Text: string
        Truncated: bool
    }

    override this.ToString() = this.Text

/// Rendering previews within `Limits`. Codecs compose these, so a value previews according to its type:
///
/// - strings are JSON-quoted and cut at `MaxStringChars`: `"first chars…"(len=5000)`;
/// - lists show `MaxItems` elements, then a count of the rest: `[1, 2, 3, …+97]`;
/// - records show fields as `(path="a.fs", line=3)`, named ones as `Match(path="a.fs", line=3)`;
///   absent optional fields are left out;
/// - union cases show their tag and fields: `done(summary="…")`;
/// - maps and JSON objects show entries: `{"a": 1, "b": 2, …+5}`;
/// - containers deeper than `MaxDepth`, or with no room for one element, collapse into a summary:
///   `Match[](len=37)`, `Match(…)`, `{…}(len=8)`;
/// - when the text would exceed `MaxChars`, whole elements are dropped first, then strings are cut;
///   numbers and summaries are shown whole or replaced by `…`.
[<RequireQualifiedAccess; CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Preview =

    /// The omission marker, a single character.
    [<Literal>]
    let Ellipsis = "…"

    /// A preview that is the whole text.
    let full (text: string) : Preview = { Text = text; Truncated = false }

    let private cut (text: string) (max: int) : string =
        if max <= 0 then
            ""
        elif text.Length <= max then
            text
        else
            let mutable n = max - 1

            if n > 0 && JsonWriter.IsHighSurrogate text[n - 1] then
                n <- n - 1

            text.Substring(0, n) + Ellipsis

    /// Enforces `MaxChars`: longer text is cut, without splitting a surrogate pair, and ends with `…`.
    let clamp (limits: Limits) (preview: Preview) : Preview =
        let max = Math.Max(limits.MaxChars, 0)

        if preview.Text.Length <= max then
            preview
        else
            {
                Text = cut preview.Text max
                Truncated = true
            }

    /// Text as is, cut to `MaxChars`.
    let text (limits: Limits) (text: string) : Preview = clamp limits (full text)

    /// Text that is shown whole or not at all (as `…`), e.g. a number: a cut number would mislead.
    let atom (limits: Limits) (text: string) : Preview =
        if text.Length <= Math.Max(limits.MaxChars, 0) then
            full text
        else
            {
                Text = cut Ellipsis limits.MaxChars
                Truncated = true
            }

    /// A collapsed container such as `Match[](len=37)`: whole, or just `…`.
    let private summary (limits: Limits) (text: string) : Preview =
        { atom limits text with
            Truncated = true
        }

    /// Whether a rendering shows nothing useful: empty, or only the omission marker.
    let private isEmpty (preview: Preview) =
        preview.Text.Length = 0 || (preview.Truncated && preview.Text = Ellipsis)

    /// A JSON-quoted string, cut at `MaxStringChars` characters or to fit `MaxChars`. A cut string
    /// ends with `…"` followed by its length, e.g. `"Lorem ipsum…"(len=5000)`.
    let string (limits: Limits) (s: string) : Preview =
        let maxChars = Math.Max(limits.MaxChars, 0)
        let maxSource = Math.Max(limits.MaxStringChars, 0)

        let whole =
            if s.Length <= maxSource then
                let quoted = JsonWriter.Quote s

                if quoted.Length <= maxChars then
                    Some(full quoted)
                else
                    None
            else
                None

        match whole with
        | Some preview -> preview
        | None ->
            let render (suffix: string) =
                // Room for the escaped prefix between `"` and `…"` + suffix.
                let budget = maxChars - 3 - suffix.Length

                if budget < 0 then
                    None
                else
                    let sb = StringBuilder "\""
                    let mutable i = 0
                    let mutable stop = false

                    while not stop && i < s.Length do
                        let before = sb.Length
                        let consumed = JsonWriter.AppendEscapedChar(sb, s, i)

                        if sb.Length - 1 > budget || i + consumed > maxSource then
                            sb.Length <- before
                            stop <- true
                        else
                            i <- i + consumed

                    sb.Append(Ellipsis).Append('"').Append(suffix) |> ignore

                    Some
                        {
                            Text = sb.ToString()
                            Truncated = true
                        }

            match render $"(len={s.Length})" with
            | Some preview -> preview
            | None ->
                match render "" with
                | Some preview -> preview
                | None -> summary limits Ellipsis

    /// Renders entries between delimiters: as many whole entries as fit, then a marker for the rest
    /// (`…+N` when `counted`, else `…`). Collapses to `summary` at `MaxDepth` or when no entry fits.
    let private container
        (limits: Limits)
        (opening: string)
        (closing: string)
        (count: int)
        (counted: bool)
        (collapsed: unit -> string)
        (entries: seq<Limits -> Preview>)
        : Preview =
        if count = 0 then
            text limits (opening + closing)
        elif limits.MaxDepth <= 0 then
            summary limits (collapsed ())
        else
            let take =
                if counted then
                    Math.Min(count, Math.Max(limits.MaxItems, 0))
                else
                    count

            let marker (rest: int) =
                if counted then $"…+{rest}" else Ellipsis

            let sb = StringBuilder opening
            let mutable budget = limits.MaxChars - opening.Length - closing.Length
            let mutable shown = 0
            let mutable truncated = false
            let mutable stop = false
            use e = (Seq.truncate take entries).GetEnumerator()

            while not stop && e.MoveNext() do
                let separator = if shown = 0 then "" else ", "
                let rest = count - shown - 1
                // Keep room for ", <marker>" in case the following entries do not fit.
                let reserve = if rest > 0 then 2 + (marker rest).Length else 0
                let room = budget - separator.Length - reserve

                if room < 1 then
                    stop <- true
                else
                    let entry =
                        e.Current
                            { limits with
                                MaxChars = room
                                MaxDepth = limits.MaxDepth - 1
                            }

                    if isEmpty entry then
                        stop <- true
                    else
                        sb.Append(separator).Append(entry.Text) |> ignore
                        budget <- budget - separator.Length - entry.Text.Length
                        shown <- shown + 1
                        truncated <- truncated || entry.Truncated

            if shown = 0 then
                summary limits (collapsed ())
            else
                if shown < count then
                    sb.Append(", ").Append(marker (count - shown)) |> ignore
                    truncated <- true

                sb.Append closing |> ignore

                clamp
                    limits
                    {
                        Text = sb.ToString()
                        Truncated = truncated
                    }

    /// Prefixes an entry with a label, e.g. `name=` or `"key": `. Renders nothing if only the label fits.
    let private labeled (label: Preview) (render: Limits -> Preview) : Limits -> Preview =
        fun limits ->
            let room = limits.MaxChars - label.Text.Length

            if room < 1 then
                { Text = ""; Truncated = true }
            else
                let inner = render { limits with MaxChars = room }

                if isEmpty inner then
                    { Text = ""; Truncated = true }
                else
                    {
                        Text = label.Text + inner.Text
                        Truncated = label.Truncated || inner.Truncated
                    }

    /// A list of `count` elements of type `itemType` (a name such as `Match` or `string`): the first
    /// `MaxItems` elements then `…+N`, or `itemType[](len=count)` when collapsed. Only the elements
    /// shown are rendered, so `items` may be long or lazy.
    let list (limits: Limits) (itemType: string) (count: int) (items: seq<Limits -> Preview>) : Preview =
        container limits "[" "]" count true (fun () -> $"{itemType}[](len={count})") items

    /// Record fields as `(name=value, …)`. Prefix a type name with `prefixed`.
    let record (limits: Limits) (fields: (string * (Limits -> Preview)) list) : Preview =
        let entries =
            fields |> List.map (fun (name, render) -> labeled (full (name + "=")) render)

        container limits "(" ")" entries.Length false (fun () -> "(…)") entries

    /// Fixed-size elements as `[a, b]`, or `[…]` when collapsed. `MaxItems` does not apply.
    let tuple (limits: Limits) (items: (Limits -> Preview) list) : Preview =
        container limits "[" "]" items.Length false (fun () -> "[…]") items

    /// A quoted map key, cut at `MaxStringChars`, followed by `: `.
    let private key (limits: Limits) (key: string) : Preview =
        let quoted =
            string
                { limits with
                    MaxChars = Int32.MaxValue / 2
                }
                key

        { quoted with
            Text = quoted.Text + ": "
        }

    /// Map entries as `{"key": value, …+N}`, or `{…}(len=count)` when collapsed.
    let map (limits: Limits) (count: int) (entries: seq<string * (Limits -> Preview)>) : Preview =
        let rendered = entries |> Seq.map (fun (k, render) -> labeled (key limits k) render)

        container limits "{" "}" count true (fun () -> $"{{…}}(len={count})") rendered

    /// `prefix` followed by a rendering that gets the remaining room, e.g. a type name before a record.
    /// Just `…` if the rendering shows nothing.
    let prefixed (prefix: string) (limits: Limits) (render: Limits -> Preview) : Preview =
        let inner =
            render
                { limits with
                    MaxChars = Math.Max(limits.MaxChars - prefix.Length, 0)
                }

        if isEmpty inner then
            summary limits Ellipsis
        else
            clamp
                limits
                { inner with
                    Text = prefix + inner.Text
                }

    /// The generic preview of a JSON value: scalars as JSON, strings cut, arrays as `any` lists and
    /// objects as maps.
    let rec ofJson (limits: Limits) (json: Json) : Preview =
        match json with
        | Json.Null -> atom limits "null"
        | Json.Bool b -> atom limits (if b then "true" else "false")
        | Json.Number n -> atom limits n.Text
        | Json.String s -> string limits s
        | Json.Array items ->
            list limits "any" (List.length items) (items |> Seq.map (fun item limits -> ofJson limits item))
        | Json.Object members ->
            map limits (List.length members) (members |> Seq.map (fun (k, v) -> k, (fun limits -> ofJson limits v)))
