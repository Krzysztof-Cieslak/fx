---
id: 01M88GRP19MFSBMEC0PVQCKQBJ
anchor: '@file'
created: 2026-11-25T04:52:31Z
norm: '1'
sig: 81590f63c7cc7524
body_hash: af48e29154a62c3b
raw_hash: 944f450bda9866b6
lines: 1-40
supersedes: 01M88BY67H6X3WN4HBSRKT987D
---

Fx.Sdk may reference only Fx.Prelude, Fx.Contracts, Fx.Effects, Fx.Variants and Fx.Models: built-in extensions see exactly what it references. tests/Fx.Extensions.Tests/ArchitectureTests.fs (sdkSurface and the SDK's project list), docs/modules/sdk.md and docs/modules/extensions.md must change with it, and each must be an SDK package (IsPackable).
