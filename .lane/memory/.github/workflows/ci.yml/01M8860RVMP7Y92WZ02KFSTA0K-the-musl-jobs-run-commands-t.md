---
id: 01M8860RVMP7Y92WZ02KFSTA0K
anchor: '@file'
created: 2026-11-25T01:44:42Z
norm: '1'
sig: 8e7430f31889b761
body_hash: 565fa8e80b29c1a0
raw_hash: 6e3a5e3063270d28
vouched: 2026-11-25T05:00:54Z
lines: 1-243
---

The musl jobs run commands through `docker exec alpine`, which drops standard input: anything piped into the binary needs `docker exec -i`.
