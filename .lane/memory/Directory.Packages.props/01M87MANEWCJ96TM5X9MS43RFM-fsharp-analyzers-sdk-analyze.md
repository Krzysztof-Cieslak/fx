---
id: 01M87MANEWCJ96TM5X9MS43RFM
anchor: '@file'
created: 2026-11-24T20:35:31Z
norm: '1'
---

FSharp.Analyzers.SDK, AnalyzersFSharpCoreVersion and the fsharp-analyzers tool in dotnet-tools.json must be upgraded together: the tool skips analyzers built against another SDK major.minor and shares its own FSharp.Core with them.
