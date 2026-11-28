---
id: 01M88A0TQW8GD4NS5P46AN08WY
anchor: '@file'
created: 2026-11-25T02:54:38Z
norm: '1'
sig: 81590f63c7cc7524
body_hash: 04998b97f8a29f81
raw_hash: f39e0584af494fb2
vouched: 2026-11-28T00:52:54Z
lines: 1-24
---

Fx.Sdk.Process may reference only Fx.Sdk, Fx.Rpc and Fx.Effects.Live: it is everything an F# extension process may use (processSdkSurface in tests/Fx.Extensions.Tests/ArchitectureTests.fs changes with it).
