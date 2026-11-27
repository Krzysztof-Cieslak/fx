---
id: 01M8DG0JJ6QHAAZWE0K6029AH0
anchor: '@file'
created: 2026-11-27T03:15:33Z
norm: '1'
sig: b6d020873baaa420
body_hash: 2bb0f9413d4e3077
raw_hash: 8e43ff74f7918223
lines: 1-515
---

Writers through two stores on one file must not rely on SQLite's busy timeout being fair: each store's writer thread takes the lock again right after each commit and the other only polls, so free-running writers starve one store past its timeout. Keep them to rounds the test starts together.
