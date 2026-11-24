/// The rules the analyzers enforce: one code per banned practice from AGENTS.md ("F# coding rules").
///
/// AOT rules keep code Native AOT safe. EFF rules are the initial "no I/O outside `Eff`" lint
/// (spec §4.2): they flag direct use of the APIs the core capabilities (`IClock`, `IIds`, `IFs`, …)
/// stand for. analyzers.json decides where each rule applies and which files may opt out.
[<RequireQualifiedAccess>]
module Fx.Analyzers.Rules

open FSharp.Compiler.Text

type Rule =
    {
        Code: string
        Title: string
        /// What to do instead; ends every message for the rule.
        Instead: string
    }

    override r.ToString() = r.Code

let private shellOptIn =
    "Capability interpreters and entry points (the imperative shell) opt in with an `allow` entry in analyzers.json."

let percentA =
    {
        Code = "AOT001"
        Title = "%A format specifier"
        Instead =
            "Use an interpolated string without a format specifier, e.g. $\"{value}\", and override `ToString` where the default is not readable."
    }

let formatString =
    {
        Code = "AOT002"
        Title = "printf or composite format string"
        Instead =
            "Use an interpolated string without printf specifiers, e.g. $\"{count} items\" (.NET formats such as {amount:N2} are fine), and `failwith $\"...\"` instead of `failwithf`."
    }

let reflection =
    {
        Code = "AOT003"
        Title = "runtime reflection"
        Instead =
            "Build behaviour from values: records, unions and combinators, and hand-written codecs instead of reflection-based serializers. If reflection is unavoidable, add an `allow` entry with the reason to analyzers.json."
    }

let clock =
    {
        Code = "EFF001"
        Title = "clock or timer outside Eff"
        Instead = $"Read time and wait through the `IClock` capability (spec §4.2). {shellOptIn}"
    }

let randomness =
    {
        Code = "EFF002"
        Title = "randomness or id generation outside Eff"
        Instead = $"Get ids and random values through the `IIds` capability (spec §4.2). {shellOptIn}"
    }

let environment =
    {
        Code = "EFF003"
        Title = "process environment outside Eff"
        Instead =
            $"Read configuration through `IVariants` and secrets through `ISecrets`; take environment facts from the session environment (spec §4.2). {shellOptIn}"
    }

let filesystem =
    {
        Code = "EFF004"
        Title = "filesystem access outside Eff"
        Instead =
            $"Use the `IFs`/`IFsRead` capability (spec §4.2); pure path manipulation with `Path` is fine. {shellOptIn}"
    }

let console =
    {
        Code = "EFF005"
        Title = "console I/O outside Eff"
        Instead =
            $"Return output as values and let the entry point or a client write it; write to a `TextWriter` you were given. {shellOptIn}"
    }

let processes =
    {
        Code = "EFF006"
        Title = "process control outside Eff"
        Instead = $"Run commands through the `IShell` capability (spec §4.2). {shellOptIn}"
    }

let network =
    {
        Code = "EFF007"
        Title = "network or IPC outside Eff"
        Instead =
            $"Reach other processes and machines through a capability such as `ILlm` or the JSON-RPC transport (spec §4.2). {shellOptIn}"
    }

let nativeCode =
    {
        Code = "EFF008"
        Title = "native code outside Eff"
        Instead =
            $"A P/Invoke can do any I/O, out of sight of the other rules: call it only from a capability interpreter (spec §4.2). {shellOptIn}"
    }

let storage =
    {
        Code = "EFF009"
        Title = "SQLite outside a storage interpreter"
        Instead =
            $"Keep data behind a store capability (`EventLog`, `SnapshotStore`, `Blobs`, …) whose SQLite interpreter lives in its own file (spec §4.2, docs/modules/store.md). {shellOptIn}"
    }

let all =
    [
        percentA
        formatString
        reflection
        clock
        randomness
        environment
        filesystem
        console
        processes
        network
        nativeCode
        storage
    ]

/// A rule violation at a place in a file.
type Finding =
    {
        Rule: Rule
        Message: string
        Range: range
    }

    override f.ToString() =
        $"{f.Rule.Code} at {f.Range}: {f.Message}"

/// A finding whose message states the problem, then what to do instead.
let finding (rule: Rule) (problem: string) (range: range) =
    {
        Rule = rule
        Message = $"{problem} {rule.Instead}"
        Range = range
    }
