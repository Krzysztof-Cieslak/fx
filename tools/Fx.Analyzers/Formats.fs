/// AOT001 and AOT002: format strings.
///
/// printf-style formatting (`sprintf`, `printfn`, `failwithf`, `kprintf`, …) runs through
/// FSharp.Core's reflection-based printf engine, and `%A` prints values by reflection; neither is
/// Native AOT safe. Interpolated strings without printf specifiers compile to `String.Concat` or
/// `String.Format` and are the replacement. Composite format strings (`String.Format("{0}", x)`)
/// are banned too: interpolation states the same thing with checked holes.
///
/// Allowed: `$"...{x}..."`, .NET formats and alignment in holes (`{x:N2}`, `{x,5}`), `%%`, and printf
/// formats without specifiers (`sprintf "text"`), which take no arguments.
module Fx.Analyzers.Formats

open System
open System.Collections.Generic
open System.Text.RegularExpressions
open FSharp.Analyzers.SDK
open FSharp.Compiler.Symbols
open FSharp.Compiler.Syntax
open FSharp.Compiler.Text
open Fx.Analyzers.Sites

let private finding = Rules.finding

let private percentA (specifier: string) range =
    finding
        Rules.percentA
        $"`{specifier}` formats values with runtime reflection, which Native AOT does not support (and `--reflectionfree` builds reject)."
        range

let private printfSpecifier (specifier: string) range =
    finding
        Rules.formatString
        $"printf format specifier `{specifier}`: printf-style formatting runs through FSharp.Core's reflection-based printf engine, which is not Native AOT safe."
        range

/// One `%` conversion: `%%`, an interpolation hole `%P()`, or a specifier such as `%-5d`.
let private conversion =
    Regex(@"%[0\-+ #]*(?:\d+|\*)?(?:\.(?:\d+|\*))?(?:%|P\(\)|[A-Za-z])", RegexOptions.CultureInvariant)

/// The printf specifiers in a printf format string, excluding `%%` and interpolation holes.
let specifiers (format: string) =
    [
        for m in conversion.Matches format do
            if not (m.Value.EndsWith('%') || m.Value.EndsWith("P()", StringComparison.Ordinal)) then
                m.Value
    ]

let private isPrintfFormat (ctor: FSharpMemberOrFunctionOrValue) =
    declaringType ctor
    |> Option.exists (fun t -> t.StartsWith("Microsoft.FSharp.Core.PrintfFormat`", StringComparison.Ordinal))

let private compositeArg =
    Regex(@"^(?:args|arg\d+)$", RegexOptions.CultureInvariant)

/// .NET composite formatting: a `format` string parameter plus `arg0`…/`args` (String.Format,
/// StringBuilder.AppendFormat, TextWriter.WriteLine(format, args), Console.WriteLine, …).
let private isCompositeFormat (m: FSharpMemberOrFunctionOrValue) =
    let parameters = m.CurriedParameterGroups |> Seq.concat |> Seq.toList

    parameters
    |> List.exists (fun p ->
        p.Name = Some "format"
        && typeIs [ "System.String"; "System.Text.CompositeFormat" ] p.Type)
    && parameters
       |> List.exists (fun p -> p.Name |> Option.exists compositeArg.IsMatch)

let private key (r: range) =
    struct (r.StartLine, r.StartColumn, r.EndLine, r.EndColumn)

let private typed (interpolated: HashSet<struct (int * int * int * int)>) (site: Site) =
    match site with
    | New(ctor, args, range) when isPrintfFormat ctor ->
        match args with
        | FSharpExprPatterns.Const(:? string as format, _) :: _ ->
            if format.Contains("%P()", StringComparison.Ordinal) then
                // Only interpolated strings produce holes: this one is typed as a printf format.
                [
                    finding
                        Rules.formatString
                        "Interpolated string used as a printf format (e.g. with `sprintf`, `printfn` or `failwithf`): it is formatted by FSharp.Core's reflection-based printf engine, which is not Native AOT safe. Pass the string itself to `failwith`, a `TextWriter` or a function taking `string`."
                        range
                ]
            elif interpolated.Contains(key range) then
                // A hole of a string-typed interpolated string; the syntax pass reports its specifier.
                []
            else
                let found = specifiers format

                [
                    match found |> List.tryFind (fun s -> s.EndsWith('A')) with
                    | Some s -> percentA s range
                    | None -> ()
                    match found |> List.tryFind (fun s -> not (s.EndsWith('A'))) with
                    | Some s -> printfSpecifier s range
                    | None -> ()
                ]
        | _ ->
            [
                finding
                    Rules.formatString
                    "printf format built at runtime: printf-style formatting runs through FSharp.Core's reflection-based printf engine, which is not Native AOT safe."
                    range
            ]
    | Call(m, _, range) when isCompositeFormat m && not (interpolated.Contains(key range)) ->
        [
            finding
                Rules.formatString
                $"Composite format string passed to `{display m}`: holes such as {{0}} are not checked by the compiler."
                range
        ]
    | _ -> []

/// Interpolated strings in the syntax tree: their ranges, and their printf specifiers (`%d{x}`),
/// which the compiler may lower to plain string concatenation before the typed tree.
let private interpolatedStrings (tree: ParsedInput) =
    let ranges = HashSet<struct (int * int * int * int)>()
    let findings = ResizeArray<Rules.Finding>()

    let walker =
        { new ASTCollecting.SyntaxCollectorBase() with
            override _.WalkExpr(_, e) =
                match e with
                | SynExpr.InterpolatedString(contents = parts; range = range) ->
                    ranges.Add(key range) |> ignore

                    for part in parts do
                        match part with
                        | SynInterpolatedStringPart.FillExpr(_,
                                                             SynInterpolationFormatting.Printf(specifier, specifierRange)) ->
                            if specifier.EndsWith('A') then
                                findings.Add(percentA specifier specifierRange)
                            else
                                findings.Add(
                                    finding
                                        Rules.formatString
                                        $"printf format specifier `{specifier}` in an interpolated string: printf-style formatting runs through FSharp.Core's reflection-based printf engine, which is not Native AOT safe."
                                        specifierRange
                                )
                        | _ -> ()
                | _ -> ()
        }

    ASTCollecting.walkAst walker tree
    ranges, List.ofSeq findings

/// AOT001 and AOT002 findings for a file.
let check (parseTree: ParsedInput) (sites: Site list) =
    let interpolated, syntax = interpolatedStrings parseTree
    syntax @ List.collect (typed interpolated) sites
