---
id: 01M87VJ840RN1N4Y8MGDJ6RKGE
anchor: '@file'
created: 2026-11-24T22:42:00Z
norm: '1'
---

The child is started as dotnet + dll when the process is dotnet, otherwise as the Fx.Aot.Scenarios apphost or native binary in AppContext.BaseDirectory. RuntimeFeature.IsDynamicCodeSupported cannot tell JIT from native: the diff runs the JIT with the AOT feature switches.
