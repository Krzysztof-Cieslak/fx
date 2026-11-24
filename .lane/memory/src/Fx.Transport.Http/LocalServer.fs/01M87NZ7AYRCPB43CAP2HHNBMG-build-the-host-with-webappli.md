---
id: 01M87NZ7AYRCPB43CAP2HHNBMG
anchor: '@file'
created: 2026-11-24T21:04:14Z
norm: '1'
sig: be1de88aa6a222c7
body_hash: 0a9f89aba2d15490
raw_hash: 26d882ca38ad88c3
vouched: 2026-11-24T21:55:28Z
lines: 1-303
---

Build the host with WebApplication.CreateEmptyBuilder, never CreateSlimBuilder/CreateBuilder: those read appsettings.json from the working directory (a cloned repo), and its Kestrel:Endpoints add listeners such as 0.0.0.0 next to the loopback one.
