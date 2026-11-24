---
id: 01M87MANEZRHJF85A48J80AT2M
anchor: '@file'
created: 2026-11-24T20:35:31Z
norm: '1'
sig: 748043cac81fd8e0
body_hash: f0e3350d56a85d93
raw_hash: a0fee062ba0e22c5
vouched: 2026-11-24T22:46:19Z
lines: 1-303
---

Type.Name is declared on System.Reflection.MemberInfo, so the System.Reflection ban needs the MemberInfo.Name allowance. The first matching ban decides the code: specific bans (Environment.TickCount, EFF001) must stay before broad ones (Environment, EFF003).
