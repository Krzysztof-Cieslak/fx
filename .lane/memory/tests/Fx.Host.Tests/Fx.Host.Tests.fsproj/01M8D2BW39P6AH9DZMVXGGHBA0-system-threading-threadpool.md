---
id: 01M8D2BW39P6AH9DZMVXGGHBA0
anchor: '@file'
created: 2026-11-26T23:17:03Z
norm: '1'
---

System.Threading.ThreadPool.ThreadTimeoutMs stays short (1 s) here: the kernel-stays live test relies on idle pool threads ending within its idle gap to catch a process tied to the thread that started it.
