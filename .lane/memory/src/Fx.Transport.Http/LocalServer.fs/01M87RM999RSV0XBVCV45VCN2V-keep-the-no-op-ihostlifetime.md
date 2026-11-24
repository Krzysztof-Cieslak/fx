---
id: 01M87RM999RSV0XBVCV45VCN2V
anchor: '@file'
created: 2026-11-24T21:50:41Z
norm: '1'
---

Keep the no-op IHostLifetime registration: the default ConsoleLifetime (even with CreateEmptyBuilder) swallows Ctrl+C and SIGTERM, stopping only the server while the process keeps running.
