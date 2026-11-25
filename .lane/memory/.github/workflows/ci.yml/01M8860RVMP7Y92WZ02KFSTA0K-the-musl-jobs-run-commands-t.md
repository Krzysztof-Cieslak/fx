---
id: 01M8860RVMP7Y92WZ02KFSTA0K
anchor: '@file'
created: 2026-11-25T01:44:42Z
norm: '1'
---

The musl jobs run commands through `docker exec alpine`, which drops standard input: anything piped into the binary needs `docker exec -i`.
