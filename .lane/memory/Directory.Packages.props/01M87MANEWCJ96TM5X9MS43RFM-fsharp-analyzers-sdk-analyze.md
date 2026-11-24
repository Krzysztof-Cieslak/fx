---
id: 01M87MANEWCJ96TM5X9MS43RFM
anchor: '@file'
created: 2026-11-24T20:35:31Z
norm: '1'
sig: 15a767339566cc27
body_hash: a7910685808e2442
raw_hash: b18cf0a5b84b1ddb
vouched: 2026-11-24T21:28:38Z
lines: 1-24
---

FSharp.Analyzers.SDK, AnalyzersFSharpCoreVersion and the fsharp-analyzers tool in dotnet-tools.json must be upgraded together: the tool skips analyzers built against another SDK major.minor and shares its own FSharp.Core with them.
