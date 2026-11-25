---
id: 01M88N91Z5NK86FZBE871BHT9Q
anchor: '@file'
created: 2026-11-25T06:11:22Z
norm: '1'
sig: 99ec769fc6dba708
body_hash: ca7b0c3026656a6d
raw_hash: b45f662819f444ec
vouched: 2026-11-25T12:56:33Z
lines: 1-503
---

Agents and extensions get only GrantsService.grants (check, request, await, withdraw); answer, grant and revoke are reached only through the client protocol with the server's caller identity. Never hand an agent environment the service itself.
