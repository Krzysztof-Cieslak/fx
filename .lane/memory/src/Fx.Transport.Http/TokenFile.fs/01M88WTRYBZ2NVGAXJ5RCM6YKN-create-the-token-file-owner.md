---
id: 01M88WTRYBZ2NVGAXJ5RCM6YKN
anchor: '@file'
created: 2026-11-25T08:23:22Z
norm: '1'
---

Create the token file owner-only from the start (UnixCreateMode 0600, an ACL at creation on Windows), never chmod after: no window in which others could open it.
