---
id: 01M87VJ1RTC288WFHHXY2C8M9Y
anchor: '@file'
created: 2026-11-24T22:41:53Z
norm: '1'
sig: b70dad81299f621a
body_hash: c2274c4a7aa9c0f2
raw_hash: 161c36c0d5182c98
lines: 1-1218
---

A call's request slot is freed only when the peer answers or the connection ends, never on timeout or cancellation: the peer counts the request until its handler finishes, so freeing early makes well-behaved peers see Overloaded.
