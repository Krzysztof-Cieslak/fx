---
id: 01M88E5JQ0Q4C8JQ8PKN67B52V
anchor: '@file'
created: 2026-11-25T04:07:08Z
norm: '1'
sig: 81590f63c7cc7524
body_hash: 2f95e31e700bd521
raw_hash: 4253ba826b3f6e0d
lines: 1-32
supersedes: 01M87Y8237NEVF36CQVMVPZA7A
---

Fx.Sdk may reference only Fx.Prelude, Fx.Contracts, Fx.Effects and Fx.Models: extensions see exactly what it references. tests/Fx.Extensions.Tests/ArchitectureTests.fs (sdkSurface and the SDK's project list) and docs/modules/extensions.md must change with it.
