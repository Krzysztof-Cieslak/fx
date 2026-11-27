---
id: 01M8D2BW39P6AH9DZMVXGGHBA0
anchor: '@file'
created: 2026-11-26T23:17:03Z
norm: '1'
sig: 81590f63c7cc7524
body_hash: 2bc187c5b857cfe9
raw_hash: 4b5ecc7c2df1da7d
vouched: 2026-11-27T12:12:20Z
lines: 1-36
---

System.Threading.ThreadPool.ThreadTimeoutMs stays short (1 s) here: the kernel-stays live test relies on idle pool threads ending within its idle gap to catch a process tied to the thread that started it.
