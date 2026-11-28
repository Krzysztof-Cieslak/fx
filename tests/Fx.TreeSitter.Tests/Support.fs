module Fx.TreeSitter.Tests.Support

open System
open System.IO
open System.Text
open System.Threading
open Expecto
open Fx.TreeSitter

let samplesDir = Path.Combine(AppContext.BaseDirectory, "Samples")

/// The sample sources, one per grammar (Samples/*, not the .outline files).
let samples: string list =
    Directory.GetFiles samplesDir
    |> Array.filter (fun f -> not (f.EndsWith(".outline", StringComparison.Ordinal)))
    |> Array.map Path.GetFileName
    |> Array.sort
    |> List.ofArray

let sampleBytes (name: string) : byte array =
    File.ReadAllBytes(Path.Combine(samplesDir, name))

let sampleLanguage (name: string) : Language =
    match Language.ofPath name with
    | Some language -> language
    | None -> failtest ("no language for " + name)

let ok (what: string) (result: Result<'T, 'E>) : 'T =
    match result with
    | Ok value -> value
    | Error error -> failtest (what + ": " + string error)

let parse (language: Language) (text: string) : Tree =
    Syntax.parseText language text CancellationToken.None |> ok "parse"

let parseSample (name: string) : Tree =
    Syntax.parse (sampleLanguage name) (sampleBytes name) CancellationToken.None
    |> ok ("parse " + name)

/// A large F# source (the F# sample repeated as modules), for cancellation and throughput.
let largeFSharp (megabytes: int) : byte array =
    let sample =
        Encoding.UTF8.GetString(sampleBytes "Retry.fs").Replace("module Billing.Retry", "")

    let builder = StringBuilder()
    let mutable i = 0

    while builder.Length < megabytes * 1_000_000 do
        builder.Append("module M").Append(i).Append(" =\n") |> ignore

        for line in sample.Split '\n' do
            builder.Append("    ").Append(line).Append('\n') |> ignore

        i <- i + 1

    Encoding.UTF8.GetBytes(builder.ToString())
