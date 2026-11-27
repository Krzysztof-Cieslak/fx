---
id: 01M87X7K8TMJT4YWT32F6AW13M
anchor: '@file'
created: 2026-11-24T23:11:08Z
norm: '1'
sig: 05d406d85eee73fe
body_hash: f7cedf4cdbf0df09
raw_hash: 838009d3a9f7d6e4
vouched: 2026-11-27T03:25:26Z
lines: 1-669
---

WaitPast registers the waiter before it reads the head: a commit after the read wakes it, one before shows in the read; the other order loses wake-ups.
