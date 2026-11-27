---
id: 01M87VJ1RNNZWHK3YJ750KSD5X
anchor: '@file'
created: 2026-11-24T22:41:53Z
norm: '1'
sig: b70dad81299f621a
body_hash: 630166c0d86f669a
raw_hash: 64e9268aabba42d1
vouched: 2026-11-27T11:48:28Z
lines: 1-1218
---

The reader never waits for a handler: responses must complete calls even while handlers wait for them. Only a full dispatch queue (MaxQueuedMessages) or outbound queue may stall it.
