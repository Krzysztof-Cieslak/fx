---
id: 01M8AMP2P578XE3YP6ZJQ9XGGA
anchor: '@file'
created: 2026-11-26T00:39:29Z
norm: '1'
sig: aea2e01ee52e4c76
body_hash: 585c8e6874e55dca
raw_hash: 1f8d95a0ddff63f2
vouched: 2026-11-27T12:12:18Z
lines: 1-401
---

fx grants grants only as the OS user signed in through local-os-user with Role.Owner, under the data directory's lock: it is a person's command line. Never reach Commands.grants (or GrantsService.grant/revoke) from an agent, an extension or a policy.
