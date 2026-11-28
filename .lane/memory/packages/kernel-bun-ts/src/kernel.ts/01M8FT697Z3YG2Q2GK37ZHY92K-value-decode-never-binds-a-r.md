---
id: 01M8FT697Z3YG2Q2GK37ZHY92K
anchor: method decodeValue
created: 2026-11-28T00:51:55Z
norm: '1'
sig: 6f673e4c04db11e7
body_hash: 611f01c88955d70e
raw_hash: cf9f8d5ecd905220
lines: 480-491
---

value/decode never binds a reserved word or one of the context's own globals (Out, console, JSON…, captured when the context is made): a text bound over them would break every later cell of the session.
