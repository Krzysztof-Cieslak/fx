---
id: 01M87X7K8RAZEVCQKZB5FE7B8K
anchor: '@file'
created: 2026-11-24T23:11:08Z
norm: '1'
sig: 05d406d85eee73fe
body_hash: 6aaaae28f946b110
raw_hash: 251f6e5d1edd38b7
vouched: 2026-11-25T04:16:00Z
lines: 1-669
---

All writes go through the one writer thread (appends, snapshot saves, checkpoints): one writing connection per database file, and WaitPast waiters are woken only after its commits.
