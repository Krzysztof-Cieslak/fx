---
id: 01M88BY67H6X3WN4HBSRKT987D
anchor: '@file'
created: 2026-11-25T03:28:08Z
norm: '1'
sig: 81590f63c7cc7524
body_hash: 5795841f680762e8
raw_hash: 27e4c73af08bc4c1
lines: 1-35
supersedes: 01M87Y8237NEVF36CQVMVPZA7A
---

Fx.Sdk may reference only Fx.Prelude, Fx.Contracts, Fx.Effects and Fx.Variants: built-in extensions see exactly what it references. tests/Fx.Extensions.Tests/ArchitectureTests.fs (sdkSurface) and docs/modules/sdk.md must change with it.
