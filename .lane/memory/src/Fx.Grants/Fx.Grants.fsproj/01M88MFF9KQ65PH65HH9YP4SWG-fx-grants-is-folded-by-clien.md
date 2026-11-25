---
id: 01M88MFF9KQ65PH65HH9YP4SWG
anchor: '@file'
created: 2026-11-25T05:57:23Z
norm: '1'
---

Fx.Grants is folded by clients (Fable): it may reference only FSharp.Core and Fx.Contracts, and use no BCL types beyond tests/Fx.Grants.Tests/ArchitectureTests.fs's allowlist. Avoid Array.* functions (they inline ArgumentNullException) and StringComparison/StartsWith(string) (culture); use the Text helpers in Pattern.fs.
