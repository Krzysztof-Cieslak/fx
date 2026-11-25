---
id: 01M88WTRYBZ2NVGAXJ5RCM6YKN
anchor: '@file'
created: 2026-11-25T08:23:22Z
norm: '1'
sig: bb606fabe03f0302
body_hash: c7dd85fe53723485
raw_hash: 4bb0c9f16d93ca09
lines: 1-95
---

Create the token file owner-only from the start (UnixCreateMode 0600, an ACL at creation on Windows), never chmod after: no window in which others could open it.
