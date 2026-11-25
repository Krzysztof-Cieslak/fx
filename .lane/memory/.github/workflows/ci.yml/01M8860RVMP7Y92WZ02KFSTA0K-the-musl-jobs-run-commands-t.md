---
id: 01M8860RVMP7Y92WZ02KFSTA0K
anchor: '@file'
created: 2026-11-25T01:44:42Z
norm: '1'
sig: 8e7430f31889b761
body_hash: b2201558f0ee073a
raw_hash: 5c2ac6c6de6524a1
lines: 1-243
---

The musl jobs run commands through `docker exec alpine`, which drops standard input: anything piped into the binary needs `docker exec -i`.
