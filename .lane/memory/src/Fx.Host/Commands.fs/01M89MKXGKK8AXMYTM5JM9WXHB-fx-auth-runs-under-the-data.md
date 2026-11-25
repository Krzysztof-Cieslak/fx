---
id: 01M89MKXGKK8AXMYTM5JM9WXHB
anchor: '@file'
created: 2026-11-25T15:19:03Z
norm: '1'
---

fx auth runs under the data directory's host lock, like a host: the file credential store stages writes under a fixed name, so only one process may write a data directory's credentials at a time.
