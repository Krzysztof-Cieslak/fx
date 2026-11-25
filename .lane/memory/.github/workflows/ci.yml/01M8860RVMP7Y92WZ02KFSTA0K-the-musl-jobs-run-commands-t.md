---
id: 01M8860RVMP7Y92WZ02KFSTA0K
anchor: '@file'
created: 2026-11-25T01:44:42Z
norm: '1'
sig: 8e7430f31889b761
body_hash: afc9ab2f20bb5be8
raw_hash: 881990e6426b4eaa
vouched: 2026-11-25T20:23:16Z
lines: 1-243
---

The musl jobs run commands through `docker exec alpine`, which drops standard input: anything piped into the binary needs `docker exec -i`.
