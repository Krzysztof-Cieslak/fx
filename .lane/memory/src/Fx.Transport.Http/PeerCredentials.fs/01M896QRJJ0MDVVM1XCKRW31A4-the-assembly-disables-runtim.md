---
id: 01M896QRJJ0MDVVM1XCKRW31A4
anchor: '@file'
created: 2026-11-25T11:16:29Z
norm: '1'
---

The assembly disables runtime marshalling: never add SetLastError = true to its DllImports (the call fails at run time on Windows), and read the pid SIO_AF_UNIX_GETPEERPID writes rather than its reported byte count (the Windows runner reported a length that is not 4).
