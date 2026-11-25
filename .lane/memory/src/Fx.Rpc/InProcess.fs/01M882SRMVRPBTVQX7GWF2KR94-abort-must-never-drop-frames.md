---
id: 01M882SRMVRPBTVQX7GWF2KR94
anchor: '@file'
created: 2026-11-25T00:48:26Z
norm: '1'
sig: b70dad81299f621a
body_hash: 67bb12de51e4fa27
raw_hash: bcf1a25759dafeb4
lines: 1-92
---

Abort must never drop frames already sent: the engine aborts right after a graceful CloseOutput, and the peer may not have read the last answers yet. The peer gets them, then End (output closed first) or a reset.
