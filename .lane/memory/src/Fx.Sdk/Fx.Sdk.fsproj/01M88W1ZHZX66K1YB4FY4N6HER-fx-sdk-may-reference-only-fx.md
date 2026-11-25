---
id: 01M88W1ZHZX66K1YB4FY4N6HER
anchor: '@file'
created: 2026-11-25T08:09:50Z
norm: '1'
sig: 81590f63c7cc7524
body_hash: 7455d5491a8edfa3
raw_hash: f9da0d1d58f8e1a3
vouched: 2026-11-25T09:29:44Z
lines: 1-48
supersedes: 01M88KH4BZA61XQP0QTBRFJSTB
---

Fx.Sdk may reference only Fx.Prelude, Fx.Contracts, Fx.Contracts.TypeScript (interpreters generate declarations, T117), Fx.Effects, Fx.Variants, Fx.Models and Fx.Grants: built-in extensions see exactly what it references. tests/Fx.Extensions.Tests/ArchitectureTests.fs (sdkSurface and the SDK's project list), docs/modules/sdk.md and docs/modules/extensions.md must change with it, and each must be an SDK package (IsPackable).
