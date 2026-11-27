---
id: 01M8FMJ8P8KE1NVY61VDPSSH0N
anchor: '@file'
created: 2026-11-27T23:13:36Z
norm: '1'
---

Windows holds every directory from the root to the parent open without FILE_SHARE_DELETE, after checking it is not a reparse point: the path-based operation that follows is only safe while they are held.
