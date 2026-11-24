---
id: 01M87RM999RSV0XBVCV45VCN2V
anchor: '@file'
created: 2026-11-24T21:50:41Z
norm: '1'
sig: be1de88aa6a222c7
body_hash: 0a9f89aba2d15490
raw_hash: 26d882ca38ad88c3
lines: 1-313
---

Keep the no-op IHostLifetime registration: the default ConsoleLifetime (even with CreateEmptyBuilder) swallows Ctrl+C and SIGTERM, stopping only the server while the process keeps running.
