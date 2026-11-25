---
id: 01M88A0TQW8GD4NS5P46AN08WY
anchor: '@file'
created: 2026-11-25T02:54:38Z
norm: '1'
sig: 81590f63c7cc7524
body_hash: e0dd159f5d38a92b
raw_hash: 4d042b556a7130f1
lines: 1-24
---

Fx.Sdk.Process may reference only Fx.Sdk, Fx.Rpc and Fx.Effects.Live: it is everything an F# extension process may use (processSdkSurface in tests/Fx.Extensions.Tests/ArchitectureTests.fs changes with it).
