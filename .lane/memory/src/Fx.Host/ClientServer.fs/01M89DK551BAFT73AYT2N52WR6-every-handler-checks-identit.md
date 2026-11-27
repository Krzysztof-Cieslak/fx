---
id: 01M89DK551BAFT73AYT2N52WR6
anchor: '@file'
created: 2026-11-25T13:16:18Z
norm: '1'
sig: aea2e01ee52e4c76
body_hash: 7c0e81f498905429
raw_hash: cda4d24e66949997
vouched: 2026-11-27T18:25:48Z
lines: 1-411
---

Every handler checks IdentityServer.caller with ClientProtocol.leastRole before acting, and a client's idempotency key is always client/<client id>/<key> (never the raw key): host-derived keys and other clients' must stay unreachable.
