---
id: 01M87MFN1BQFSAEYMWZ4JAA01G
anchor: '@file'
created: 2026-11-24T20:38:15Z
norm: '1'
---

In GitHub Actions, fsharp-analyzers output must be captured and stripped of ANSI colour codes before printing: the tool colours its ::error lines and the runner ignores commands that do not start the line.
