---
id: 01M88DRS8DSR6K2JB41JDF5J3J
anchor: '@file'
created: 2026-11-25T04:00:08Z
norm: '1'
sig: 538d9858d47cb6bf
body_hash: f8311f9a03b75f4f
raw_hash: 9638e8d06800d3b3
lines: 1-317
---

ChatAggregate.apply must stay pure and total: a stream that breaks the rules becomes violations and then InvalidResponse, never an exception, because recorded streams are replayed through it.
