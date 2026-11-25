---
id: 01M89ABWMWKE71G5GAGTGY873N
anchor: '@file'
created: 2026-11-25T12:19:55Z
norm: '1'
---

Auth methods never get the credential store: the host reads and writes credentials for them, so it is the single refresher (Resolution.Updated).
