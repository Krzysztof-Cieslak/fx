---
id: 01M88MYB7AF7SHTHC45247FQQT
anchor: '@file'
created: 2026-11-25T06:05:31Z
norm: '1'
supersedes: 01M88GRP19MFSBMEC0PVQCKQBJ
---

Fx.Sdk may reference only Fx.Prelude, Fx.Contracts, Fx.Effects, Fx.Variants, Fx.Models and Fx.Grants: built-in extensions see exactly what it references. tests/Fx.Extensions.Tests/ArchitectureTests.fs (sdkSurface and the SDK's project list), docs/modules/sdk.md and docs/modules/extensions.md must change with it, and each must be an SDK package (IsPackable).
