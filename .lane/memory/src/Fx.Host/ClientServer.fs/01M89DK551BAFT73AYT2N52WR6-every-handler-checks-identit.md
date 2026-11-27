---
id: 01M89DK551BAFT73AYT2N52WR6
anchor: '@file'
created: 2026-11-25T13:16:18Z
norm: '1'
sig: aea2e01ee52e4c76
body_hash: 2d33b6d9731e8b69
raw_hash: e1bc8b518f29612a
vouched: 2026-11-27T04:24:44Z
lines: 1-411
---

Every handler checks IdentityServer.caller with ClientProtocol.leastRole before acting, and a client's idempotency key is always client/<client id>/<key> (never the raw key): host-derived keys and other clients' must stay unreachable.
