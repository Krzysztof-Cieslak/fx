---
id: 01M87VJ1RNNZWHK3YJ750KSD5X
anchor: '@file'
created: 2026-11-24T22:41:53Z
norm: '1'
sig: b70dad81299f621a
body_hash: c2274c4a7aa9c0f2
raw_hash: 161c36c0d5182c98
lines: 1-1218
---

The reader never waits for a handler: responses must complete calls even while handlers wait for them. Only a full dispatch queue (MaxQueuedMessages) or outbound queue may stall it.
