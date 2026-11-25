---
id: 01M89ABWMWKE71G5GAGTGY873N
anchor: '@file'
created: 2026-11-25T12:19:55Z
norm: '1'
sig: 49d5b330b4dae107
body_hash: b7e08f13c850606d
raw_hash: 8028e08cea648065
lines: 1-365
---

Auth methods never get the credential store: the host reads and writes credentials for them, so it is the single refresher (Resolution.Updated).
