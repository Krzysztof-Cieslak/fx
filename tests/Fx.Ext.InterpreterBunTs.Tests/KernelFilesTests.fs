/// The kernel's files carried in the binary: `extensions/Fx.Ext.InterpreterBunTs/KernelFiles.fs` is
/// generated here from packages/kernel-bun-ts/src, and must match it (`UPDATE_GOLDEN=1` rewrites it).
module Fx.Ext.InterpreterBunTs.Tests.KernelFilesTests

open System
open System.IO
open System.Text
open Expecto
open Fx.Ext.InterpreterBunTs
open Fx.Kernel.Tests

/// The kernel's sources: every `.ts` file of the package's `src` but the generated protocol types,
/// which are only imported as types (erased when Bun loads the kernel).
let sources () : (string * string) list =
    Directory.GetFiles(Path.Combine(Repo.kernelPackage, "src"), "*.ts")
    |> Array.map (fun file -> Path.GetFileName file)
    |> Array.filter (fun name -> not (name.EndsWith(".d.ts", StringComparison.Ordinal)))
    |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b))
    |> Array.map (fun name -> name, File.ReadAllText(Path.Combine(Repo.kernelPackage, "src", name)))
    |> List.ofArray

let private literal (line: string) : string =
    let text = StringBuilder("\"")

    for c in line do
        match c with
        | '\\' -> text.Append "\\\\" |> ignore
        | '"' -> text.Append "\\\"" |> ignore
        | '\t' -> text.Append "\\t" |> ignore
        | c when c < ' ' || c = '\u007f' -> text.Append("\\u").Append((int c).ToString("x4")) |> ignore
        | c -> text.Append c |> ignore

    text.Append('"').ToString()

let private identifier (name: string) =
    String(
        name
        |> Seq.map (fun c -> if Char.IsAsciiLetterOrDigit c then c else '_')
        |> Array.ofSeq
    )

/// The F# source that carries the files: each file as an array of its lines (so that the formatter
/// keeps it as it is), joined with `\n`.
let render (files: (string * string) list) : string =
    let lines = ResizeArray<string>()

    lines.AddRange
        [
            "// Generated from packages/kernel-bun-ts/src by tests/Fx.Ext.InterpreterBunTs.Tests. Do not edit: change"
            "// the kernel's sources and run the tests with UPDATE_GOLDEN=1."
            ""
            "/// The bun-ts kernel's files (packages/kernel-bun-ts/src, without the generated protocol types), as"
            "/// lines joined with `\\n` (the repository checks them out with `\\n`, .gitattributes)."
            "module Fx.Ext.InterpreterBunTs.KernelFiles"
            ""
        ]

    for name, content in files do
        lines.Add $"let private {identifier name} ="
        lines.Add "    [|"

        for line in content.Split '\n' do
            lines.Add("        " + literal line)

        lines.Add "    |]"
        lines.Add ""

    lines.Add "/// Every file: its name and content."
    lines.Add "let all: (string * string) list ="
    lines.Add "    ["

    for name, _ in files do
        lines.Add $"        \"{name}\", String.concat \"\\n\" {identifier name}"

    lines.Add "    ]"
    String.Join("\n", lines) + "\n"

[<Tests>]
let tests =
    testList
        "kernel files"
        [
            testCase "KernelFiles.fs is generated from the kernel's sources"
            <| fun () ->
                Repo.expectGenerated [ "extensions"; "Fx.Ext.InterpreterBunTs"; "KernelFiles.fs" ] (render (sources ()))

            testCase "the assets are the kernel's sources, byte for byte"
            <| fun () ->
                let assets = BunTs.assets |> List.map (fun a -> a.Name, a.Content)
                Expect.equal assets (sources ()) "the sources"

            testCase "the entry point is an asset"
            <| fun () -> Expect.isTrue (BunTs.assets |> List.exists (fun a -> a.Name = "main.ts")) "main.ts"
        ]
