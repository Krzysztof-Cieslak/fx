---
id: 01M895DH49PKEJ9HT9HVF652AZ
anchor: '@file'
created: 2026-11-25T10:53:26Z
norm: '1'
sig: 4edb65de7bd06175
body_hash: bada9704404d350b
raw_hash: ea65db5644f8f770
vouched: 2026-11-26T23:26:19Z
lines: 1-109
---

Each kernel launch gets its own owned directories (EnvHands.launchName: the session hashed, then the launch's number): RuntimeSpec.prepare expects fresh ones, and a restarted kernel must not reuse a dead one's.
