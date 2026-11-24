/// The analyzers the `fsharp-analyzers` host discovers. Each reports its rules as errors, filtered
/// by analyzers.json (scope and allowlist).
module Fx.Analyzers.Analyzers

open FSharp.Analyzers.SDK

let private codes = Rules.all |> List.map (fun r -> r.Code)

/// Runs `findings` on a file and turns what analyzers.json lets through into messages.
let private run (name: string) (findings: CliContext -> Rules.Finding list) (ctx: CliContext) =
    async {
        match Config.forFile codes ctx.FileName with
        | Error errors ->
            let details = String.concat "; " errors
            return failwith $"{Config.FileName} is invalid: {details}"
        | Ok located ->
            let path = Config.relativePath located ctx.FileName

            return
                findings ctx
                |> List.filter (fun f -> Config.reports located.Config f.Rule.Code path)
                |> List.distinctBy (fun f -> f.Rule.Code, f.Range)
                |> List.sortBy (fun f -> f.Range.StartLine, f.Range.StartColumn, f.Rule.Code)
                |> List.map (fun f ->
                    {
                        Type = name
                        Message = f.Message
                        Code = f.Rule.Code
                        Severity = Severity.Error
                        Range = f.Range
                        Fixes = []
                    })
    }

let private sites (ctx: CliContext) =
    ctx.TypedTree |> Option.map Sites.ofFile |> Option.defaultValue []

[<Literal>]
let FormatStringsName = "FormatStrings"

[<Literal>]
let RuntimeReflectionName = "RuntimeReflection"

[<Literal>]
let EffectsName = "EffectsOutsideEff"

[<CliAnalyzer(FormatStringsName, "AOT001 %A format specifier; AOT002 printf or composite format string")>]
let formatStrings: Analyzer<CliContext> =
    run FormatStringsName (fun ctx -> Formats.check ctx.ParseFileResults.ParseTree (sites ctx))

[<CliAnalyzer(RuntimeReflectionName, "AOT003 runtime reflection")>]
let runtimeReflection: Analyzer<CliContext> =
    run RuntimeReflectionName (fun ctx -> Apis.check [ Rules.reflection ] (sites ctx))

let private effectRules =
    Rules.all
    |> List.filter (fun r -> r.Code.StartsWith("EFF", System.StringComparison.Ordinal))

[<CliAnalyzer(EffectsName,
              "EFF001-EFF008 clock, randomness, environment, filesystem, console, processes, network or native code outside Eff")>]
let effects: Analyzer<CliContext> =
    run EffectsName (fun ctx -> Apis.check effectRules (sites ctx))

/// Every analyzer with its name, for tests.
let all =
    [
        FormatStringsName, formatStrings
        RuntimeReflectionName, runtimeReflection
        EffectsName, effects
    ]
