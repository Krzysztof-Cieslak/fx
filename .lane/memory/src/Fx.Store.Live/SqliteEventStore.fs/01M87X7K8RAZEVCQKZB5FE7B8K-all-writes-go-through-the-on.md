---
id: 01M87X7K8RAZEVCQKZB5FE7B8K
anchor: '@file'
created: 2026-11-24T23:11:08Z
norm: '1'
sig: 05d406d85eee73fe
body_hash: 2bcd69f026bf3da2
raw_hash: 0155221a6ef56f32
vouched: 2026-11-24T23:48:02Z
lines: 1-669
---

All writes go through the one writer thread (appends, snapshot saves, checkpoints): one writing connection per database file, and WaitPast waiters are woken only after its commits.
