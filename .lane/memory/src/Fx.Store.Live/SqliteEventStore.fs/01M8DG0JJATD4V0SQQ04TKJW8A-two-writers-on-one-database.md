---
id: 01M8DG0JJATD4V0SQQ04TKJW8A
anchor: '@file'
created: 2026-11-27T03:15:33Z
norm: '1'
---

Two writers on one database file are never fair, two stores in one process included (SQLite polls for the write lock, no queue): if the product ever opens two stores on one file, give them one writer thread or a FIFO gate per file first.
