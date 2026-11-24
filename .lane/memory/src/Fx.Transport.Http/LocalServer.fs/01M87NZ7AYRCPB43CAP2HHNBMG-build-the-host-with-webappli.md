---
id: 01M87NZ7AYRCPB43CAP2HHNBMG
anchor: '@file'
created: 2026-11-24T21:04:14Z
norm: '1'
---

Build the host with WebApplication.CreateEmptyBuilder, never CreateSlimBuilder/CreateBuilder: those read appsettings.json from the working directory (a cloned repo), and its Kestrel:Endpoints add listeners such as 0.0.0.0 next to the loopback one.
