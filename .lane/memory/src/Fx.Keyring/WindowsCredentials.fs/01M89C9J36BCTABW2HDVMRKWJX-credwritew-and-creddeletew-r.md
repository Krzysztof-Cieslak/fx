---
id: 01M89C9J36BCTABW2HDVMRKWJX
anchor: '@file'
created: 2026-11-25T12:53:35Z
norm: '1'
sig: bd4963372dff3b9d
body_hash: 846a8a70ae2e7eaa
raw_hash: 04e452c99ec124ab
lines: 1-197
---

CredWriteW and CredDeleteW run one at a time in the process: concurrent writes of different targets lost one on windows-latest while every call reported success.
