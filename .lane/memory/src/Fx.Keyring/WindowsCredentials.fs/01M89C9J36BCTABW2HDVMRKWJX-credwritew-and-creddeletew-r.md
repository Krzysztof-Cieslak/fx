---
id: 01M89C9J36BCTABW2HDVMRKWJX
anchor: '@file'
created: 2026-11-25T12:53:35Z
norm: '1'
---

CredWriteW and CredDeleteW run one at a time in the process: concurrent writes of different targets lost one on windows-latest while every call reported success.
