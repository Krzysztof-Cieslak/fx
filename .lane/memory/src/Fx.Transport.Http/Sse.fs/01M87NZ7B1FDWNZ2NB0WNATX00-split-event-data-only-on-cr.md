---
id: 01M87NZ7B1FDWNZ2NB0WNATX00
anchor: '@file'
created: 2026-11-24T21:04:14Z
norm: '1'
sig: 669dd8fd6d4681f4
body_hash: 2abf93dad3e5d023
raw_hash: 0795b6f914a32a23
lines: 1-54
---

Split event data only on CR, LF and CRLF; String.ReplaceLineEndings also splits U+2028/U+0085/FF and would corrupt data.
