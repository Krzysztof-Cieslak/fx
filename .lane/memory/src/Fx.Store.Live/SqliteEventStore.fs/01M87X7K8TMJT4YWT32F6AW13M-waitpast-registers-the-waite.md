---
id: 01M87X7K8TMJT4YWT32F6AW13M
anchor: '@file'
created: 2026-11-24T23:11:08Z
norm: '1'
---

WaitPast registers the waiter before it reads the head: a commit after the read wakes it, one before shows in the read; the other order loses wake-ups.
