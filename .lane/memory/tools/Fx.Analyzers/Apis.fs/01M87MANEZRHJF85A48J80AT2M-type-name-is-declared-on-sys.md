---
id: 01M87MANEZRHJF85A48J80AT2M
anchor: '@file'
created: 2026-11-24T20:35:31Z
norm: '1'
sig: 2f93e74691977137
body_hash: 0239c66179989b00
raw_hash: 3b4d75795704c7da
vouched: 2026-11-24T23:57:42Z
lines: 1-303
---

Type.Name is declared on System.Reflection.MemberInfo, so the System.Reflection ban needs the MemberInfo.Name allowance. The first matching ban decides the code: specific bans (Environment.TickCount, EFF001) must stay before broad ones (Environment, EFF003).
