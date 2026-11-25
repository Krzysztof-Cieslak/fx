---
id: 01M88768SSZ0QJ2WSM0QP789TD
anchor: '@file'
created: 2026-11-25T02:05:10Z
norm: '1'
sig: f1ee8a2ef0996b8b
body_hash: 9614c71064618966
raw_hash: ea1075f5e45dfbe9
lines: 1-176
---

Kernel stdout carries both protocol frames and stray bytes from cells; frames must be recovered after the prefix {"jsonrpc":"2.0", and everything else reported, never treated as a protocol violation (only an oversized message ends the connection).
