---
id: 01M87MANEZRHJF85A48J80AT2M
anchor: '@file'
created: 2026-11-24T20:35:31Z
norm: '1'
---

Type.Name is declared on System.Reflection.MemberInfo, so the System.Reflection ban needs the MemberInfo.Name allowance. The first matching ban decides the code: specific bans (Environment.TickCount, EFF001) must stay before broad ones (Environment, EFF003).
