---
id: 01M88H67YM5FHPH8DAZDV4ZKMJ
anchor: '@file'
created: 2026-11-25T04:59:55Z
norm: '1'
sig: 81590f63c7cc7524
body_hash: b1384dc575560786
raw_hash: ebd43c23df5a274b
lines: 1-41
supersedes: 01M88BY67H6X3WN4HBSRKT987D
---

Fx.Sdk may reference only Fx.Prelude, Fx.Contracts, Fx.Contracts.TypeScript (for interpreters, T117), Fx.Effects and Fx.Variants: built-in extensions see exactly what it references. tests/Fx.Extensions.Tests/ArchitectureTests.fs (sdkSurface and the project-reference test) and docs/modules/sdk.md must change with it.
