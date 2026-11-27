---
id: 01M8FMJ8P8KE1NVY61VDPSSH0N
anchor: '@file'
created: 2026-11-27T23:13:36Z
norm: '1'
sig: 9c3fdaa722f99c3a
body_hash: f5b11cc334b03fbf
raw_hash: c0525419f5a239db
lines: 1-260
---

Windows holds every directory from the root to the parent open without FILE_SHARE_DELETE, after checking it is not a reparse point: the path-based operation that follows is only safe while they are held.
