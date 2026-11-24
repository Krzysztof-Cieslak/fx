---
id: 01M87MANEXEXAZ5J58PQHNP29K
anchor: '@file'
created: 2026-11-24T20:35:31Z
norm: '1'
sig: 81590f63c7cc7524
body_hash: 70ae2d332d20db10
raw_hash: 4b2baf86f5fd5a42
lines: 1-21
---

Build against AnalyzersFSharpCoreVersion, not the repository FSharp.Core: the fsharp-analyzers host loads analyzers into its own FSharp.Core, so a newer one breaks the Analyzer<CliContext> type identity. The assembly name must contain "Analyzer" and not "test", or the CLI does not load it.
