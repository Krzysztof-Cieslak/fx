---
id: 01M88KH4BZA61XQP0QTBRFJSTB
anchor: '@file'
created: 2026-11-25T05:40:49Z
norm: '1'
sig: 81590f63c7cc7524
body_hash: 01ad985610d2a45f
raw_hash: 125b0934dc6b6760
lines: 1-44
supersedes: 01M88GRP19MFSBMEC0PVQCKQBJ
---

Fx.Sdk may reference only Fx.Prelude, Fx.Contracts, Fx.Contracts.TypeScript (interpreters generate declarations, T117), Fx.Effects, Fx.Variants and Fx.Models: built-in extensions see exactly what it references. tests/Fx.Extensions.Tests/ArchitectureTests.fs (sdkSurface and the SDK project list), docs/modules/sdk.md and docs/modules/extensions.md must change with it, and each must be an SDK package (IsPackable).
