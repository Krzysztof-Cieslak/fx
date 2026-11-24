---
id: 01M87X7K8RAZEVCQKZB5FE7B8K
anchor: '@file'
created: 2026-11-24T23:11:08Z
norm: '1'
---

All writes go through the one writer thread (appends, snapshot saves, checkpoints): one writing connection per database file, and WaitPast waiters are woken only after its commits.
