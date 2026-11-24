---
id: 01M87VJ1RNNZWHK3YJ750KSD5X
anchor: '@file'
created: 2026-11-24T22:41:53Z
norm: '1'
---

The reader never waits for a handler: responses must complete calls even while handlers wait for them. Only a full dispatch queue (MaxQueuedMessages) or outbound queue may stall it.
