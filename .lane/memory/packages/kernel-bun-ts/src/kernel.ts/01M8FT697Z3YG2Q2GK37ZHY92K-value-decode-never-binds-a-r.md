---
id: 01M8FT697Z3YG2Q2GK37ZHY92K
anchor: method decodeValue
created: 2026-11-28T00:51:55Z
norm: '1'
---

value/decode never binds a reserved word or one of the context's own globals (Out, console, JSON…, captured when the context is made): a text bound over them would break every later cell of the session.
