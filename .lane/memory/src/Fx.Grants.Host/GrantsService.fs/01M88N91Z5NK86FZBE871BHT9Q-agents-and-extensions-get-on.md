---
id: 01M88N91Z5NK86FZBE871BHT9Q
anchor: '@file'
created: 2026-11-25T06:11:22Z
norm: '1'
---

Agents and extensions get only GrantsService.grants (check, request, await, withdraw); answer, grant and revoke are reached only through the client protocol with the server's caller identity. Never hand an agent environment the service itself.
