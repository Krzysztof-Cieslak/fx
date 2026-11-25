---
id: 01M89MKXGKK8AXMYTM5JM9WXHB
anchor: '@file'
created: 2026-11-25T15:19:03Z
norm: '1'
sig: aea2e01ee52e4c76
body_hash: 4e248dff75c1c499
raw_hash: 5256226e4c6328c7
vouched: 2026-11-25T23:30:35Z
lines: 1-184
---

fx auth runs under the data directory's host lock, like a host: the file credential store stages writes under a fixed name, so only one process may write a data directory's credentials at a time.
