---
id: 01M89DK551BAFT73AYT2N52WR6
anchor: '@file'
created: 2026-11-25T13:16:18Z
norm: '1'
sig: aea2e01ee52e4c76
body_hash: 441704770aa9c89f
raw_hash: fa7f436d900ff66c
vouched: 2026-11-25T20:43:47Z
lines: 1-411
---

Every handler checks IdentityServer.caller with ClientProtocol.leastRole before acting, and a client's idempotency key is always client/<client id>/<key> (never the raw key): host-derived keys and other clients' must stay unreachable.
