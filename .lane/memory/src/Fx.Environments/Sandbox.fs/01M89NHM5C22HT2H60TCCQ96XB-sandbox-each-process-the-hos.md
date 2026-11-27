---
id: 01M89NHM5C22HT2H60TCCQ96XB
anchor: '@file'
created: 2026-11-25T15:35:17Z
norm: '1'
sig: db88ec098994fb1c
body_hash: 3f9ae5d4b5d5252c
raw_hash: 23028231e157e932
vouched: 2026-11-27T02:59:27Z
lines: 1-349
---

Sandbox each process the host starts for an agent (EnvHandle.Client), never envd itself: envd may be in the host's process, writes its own state, and serves processes with different policies (an agent's overrides).
