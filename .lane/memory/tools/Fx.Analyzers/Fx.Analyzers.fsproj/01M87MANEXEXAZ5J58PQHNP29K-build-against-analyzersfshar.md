---
id: 01M87MANEXEXAZ5J58PQHNP29K
anchor: '@file'
created: 2026-11-24T20:35:31Z
norm: '1'
---

Build against AnalyzersFSharpCoreVersion, not the repository FSharp.Core: the fsharp-analyzers host loads analyzers into its own FSharp.Core, so a newer one breaks the Analyzer<CliContext> type identity. The assembly name must contain "Analyzer" and not "test", or the CLI does not load it.
