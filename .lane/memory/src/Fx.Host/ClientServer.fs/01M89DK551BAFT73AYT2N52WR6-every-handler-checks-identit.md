---
id: 01M89DK551BAFT73AYT2N52WR6
anchor: '@file'
created: 2026-11-25T13:16:18Z
norm: '1'
sig: aea2e01ee52e4c76
body_hash: 614de14258e40994
raw_hash: 0a7071f538b0784d
vouched: 2026-11-27T12:12:18Z
lines: 1-411
---

Every handler checks IdentityServer.caller with ClientProtocol.leastRole before acting, and a client's idempotency key is always client/<client id>/<key> (never the raw key): host-derived keys and other clients' must stay unreachable.
