---
id: 01M87MFN1BQFSAEYMWZ4JAA01G
anchor: '@file'
created: 2026-11-24T20:38:15Z
norm: '1'
sig: 977c0f8898e6cb06
body_hash: e33b8923ed13e902
raw_hash: 0fb2c5358611e08d
lines: 1-131
---

In GitHub Actions, fsharp-analyzers output must be captured and stripped of ANSI colour codes before printing: the tool colours its ::error lines and the runner ignores commands that do not start the line.
