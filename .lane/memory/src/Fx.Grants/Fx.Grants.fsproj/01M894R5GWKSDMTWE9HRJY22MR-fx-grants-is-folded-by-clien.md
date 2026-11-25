---
id: 01M894R5GWKSDMTWE9HRJY22MR
anchor: '@file'
created: 2026-11-25T10:41:46Z
norm: '1'
supersedes: 01M88MFF9KQ65PH65HH9YP4SWG
---

Fx.Grants is folded by clients (Fable): it may reference only FSharp.Core, Fx.Contracts and Fx.Identity (a principal's kind, from its id), and use no BCL types beyond tests/Fx.Grants.Tests/ArchitectureTests.fs's allowlist. Avoid Array.* functions (they inline ArgumentNullException) and StringComparison/StartsWith(string) (culture); use the Text helpers in Pattern.fs.
