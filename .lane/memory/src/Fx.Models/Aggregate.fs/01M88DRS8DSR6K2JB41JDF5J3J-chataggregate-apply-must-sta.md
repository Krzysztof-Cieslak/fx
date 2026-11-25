---
id: 01M88DRS8DSR6K2JB41JDF5J3J
anchor: '@file'
created: 2026-11-25T04:00:08Z
norm: '1'
---

ChatAggregate.apply must stay pure and total: a stream that breaks the rules becomes violations and then InvalidResponse, never an exception, because recorded streams are replayed through it.
