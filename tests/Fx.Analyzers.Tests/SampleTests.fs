/// Failing samples prove each rule; the clean sample proves what stays allowed.
///
/// Each script in Samples/ is type-checked like the `fsharp-analyzers` CLI does and run through every
/// analyzer with the repository's analyzers.json. A line ending in `// expect: <codes>` must produce
/// exactly those codes; every other line must produce none.
module Fx.Analyzers.Tests.SampleTests

open System
open System.IO
open System.Text.RegularExpressions
open Expecto
open FSharp.Analyzers.SDK
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Text
open Fx.Analyzers

let private samplesDir =
    Path.Combine(Repo.root, "tests", "Fx.Analyzers.Tests", "Samples")

let private samples =
    Directory.GetFiles(samplesDir, "*.fsx") |> Array.sort |> List.ofArray

let private checker = lazy (FSharpChecker.Create(keepAssemblyContents = true))

/// The context the CLI builds for a script: type-checked, with typed and untyped trees.
let private context (path: string) =
    async {
        let checker = checker.Value
        let source = SourceText.ofString (File.ReadAllText path)

        let! options, _ =
            checker.GetProjectOptionsFromScript(path, source, assumeDotNetFramework = false)

        let! parse, answer = checker.ParseAndCheckFileInProject(path, 0, source, options)

        match answer with
        | FSharpCheckFileAnswer.Aborted -> return failwith $"type checking {path} was aborted"
        | FSharpCheckFileAnswer.Succeeded check ->
            let errors =
                check.Diagnostics
                |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)
                |> Array.map string

            if errors.Length > 0 then
                let details = String.concat "\n" errors
                failwith $"samples must type-check:\n{details}"

            let! project = checker.ParseAndCheckProject options
            return Utils.createContext project path source (parse, check) (BackgroundCompilerOptions options)
    }

let private marker =
    Regex(@"//\s*expect:\s*(?<codes>[A-Z0-9, ]+)$", RegexOptions.CultureInvariant)

/// `"<line> <code>"` for every code the sample's `expect` markers name.
let private expected (path: string) =
    File.ReadAllLines path
    |> Array.indexed
    |> Array.toList
    |> List.collect (fun (i, line) ->
        let m = marker.Match line

        if m.Success then
            m.Groups["codes"].Value.Split([| ' '; ',' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.toList
            |> List.map (fun code -> $"{i + 1} {code}")
        else
            [])
    |> List.distinct
    |> List.sort

let private messages (path: string) =
    async {
        let! ctx = context path

        let! results =
            Analyzers.all
            |> List.map (fun (_, analyzer) -> analyzer ctx)
            |> Async.Sequential

        return List.concat results
    }

let private found (messages: Message list) =
    messages
    |> List.map (fun m -> $"{m.Range.StartLine} {m.Code}")
    |> List.distinct
    |> List.sort

[<Tests>]
let tests =
    testList
        "Samples"
        [
            for path in samples do
                testAsync (Path.GetFileName path) {
                    let! messages = messages path
                    Expect.equal (found messages) (expected path) "findings must match the `expect` markers"

                    for m in messages do
                        let rule = Rules.all |> List.find (fun r -> r.Code = m.Code)
                        Expect.isTrue (m.Message.EndsWith rule.Instead) $"{m.Code} says what to do instead"
                        Expect.equal m.Severity Severity.Error "findings are errors, so CI fails"
                }

            test "every rule has a failing sample" {
                let covered =
                    samples
                    |> List.collect expected
                    |> List.map (fun e -> e.Split(' ')[1])
                    |> Set.ofList

                for rule in Rules.all do
                    Expect.isTrue (covered.Contains rule.Code) $"no sample expects {rule.Code}"
            }

            test "the clean sample expects nothing" {
                let clean = Path.Combine(samplesDir, "Clean.fsx")
                Expect.isTrue (File.Exists clean) "Clean.fsx exists"
                Expect.isEmpty (expected clean) "no markers"
            }
        ]
