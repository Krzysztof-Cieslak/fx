---
id: 01M88DRS8DSR6K2JB41JDF5J3J
anchor: '@file'
created: 2026-11-25T04:00:08Z
norm: '1'
sig: 538d9858d47cb6bf
body_hash: 354711e8b2d66dc3
raw_hash: e8d070eae0a2a613
vouched: 2026-11-25T07:46:29Z
lines: 1-317
---

ChatAggregate.apply must stay pure and total: a stream that breaks the rules becomes violations and then InvalidResponse, never an exception, because recorded streams are replayed through it.
