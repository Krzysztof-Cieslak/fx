---
id: 01M894R5HTAP2F30WXAHNX6VE8
anchor: '@file'
created: 2026-11-25T10:41:46Z
norm: '1'
sig: 81590f63c7cc7524
body_hash: 9df6a33cb4607f4f
raw_hash: 786f33dacd20fd30
vouched: 2026-11-25T20:23:16Z
lines: 1-48
supersedes: 01M88W1ZHZX66K1YB4FY4N6HER
---

Fx.Sdk may reference only Fx.Prelude, Fx.Contracts, Fx.Contracts.TypeScript (interpreters generate declarations, T117), Fx.Effects, Fx.Variants, Fx.Models, Fx.Grants and Fx.Identity (identity providers, T122): built-in extensions see exactly what it references. tests/Fx.Extensions.Tests/ArchitectureTests.fs (sdkSurface and the SDK's project list), docs/modules/sdk.md and docs/modules/extensions.md must change with it, and each must be an SDK package (IsPackable).
