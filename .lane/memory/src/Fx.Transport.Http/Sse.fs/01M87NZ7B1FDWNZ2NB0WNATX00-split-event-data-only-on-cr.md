---
id: 01M87NZ7B1FDWNZ2NB0WNATX00
anchor: '@file'
created: 2026-11-24T21:04:14Z
norm: '1'
---

Split event data only on CR, LF and CRLF; String.ReplaceLineEndings also splits U+2028/U+0085/FF and would corrupt data.
