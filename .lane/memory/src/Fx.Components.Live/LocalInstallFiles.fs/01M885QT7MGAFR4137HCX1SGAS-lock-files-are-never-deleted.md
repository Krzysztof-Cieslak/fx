---
id: 01M885QT7MGAFR4137HCX1SGAS
anchor: tryLock
created: 2026-11-25T01:39:48Z
norm: '1'
---

Lock files are never deleted: deleting one while another process opens it splits the lock. FileShare.None is flock(LOCK_EX) on Linux and macOS, also between two handles of one process.
