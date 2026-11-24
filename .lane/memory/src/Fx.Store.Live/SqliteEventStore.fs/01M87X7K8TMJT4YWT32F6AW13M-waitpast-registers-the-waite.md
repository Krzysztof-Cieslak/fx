---
id: 01M87X7K8TMJT4YWT32F6AW13M
anchor: '@file'
created: 2026-11-24T23:11:08Z
norm: '1'
sig: 05d406d85eee73fe
body_hash: 2bcd69f026bf3da2
raw_hash: 0155221a6ef56f32
vouched: 2026-11-24T23:48:02Z
lines: 1-669
---

WaitPast registers the waiter before it reads the head: a commit after the read wakes it, one before shows in the read; the other order loses wake-ups.
