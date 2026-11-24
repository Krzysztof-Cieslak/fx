---
id: 01M87JT6KX48YRRH0EX2FTRD3T
anchor: '@file'
created: 2026-11-24T20:09:03Z
norm: '1'
sig: 15a767339566cc27
body_hash: b1874690e8628cf0
raw_hash: 805244a0bc16b2ea
lines: 1-17
---

EnableExpectoTestingPlatformIntegration must stay true while global.json selects Microsoft Testing Platform; otherwise dotnet test refuses to run the Expecto projects (they fall back to VSTest).
