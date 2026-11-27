---
id: 01M8DG0JJ6QHAAZWE0K6029AH0
anchor: '@file'
created: 2026-11-27T03:15:33Z
norm: '1'
---

Writers through two stores on one file must not rely on SQLite's busy timeout being fair: each store's writer thread takes the lock again right after each commit and the other only polls, so free-running writers starve one store past its timeout. Keep them to rounds the test starts together.
