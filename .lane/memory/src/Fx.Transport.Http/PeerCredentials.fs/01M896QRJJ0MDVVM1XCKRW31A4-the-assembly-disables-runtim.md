---
id: 01M896QRJJ0MDVVM1XCKRW31A4
anchor: '@file'
created: 2026-11-25T11:16:29Z
norm: '1'
sig: d18d3e04406c25c6
body_hash: da90041ff7f6f9f9
raw_hash: 9f44509f95e2a9aa
lines: 1-247
---

The assembly disables runtime marshalling: never add SetLastError = true to its DllImports (the call fails at run time on Windows), and read the pid SIO_AF_UNIX_GETPEERPID writes rather than its reported byte count (the Windows runner reported a length that is not 4).
