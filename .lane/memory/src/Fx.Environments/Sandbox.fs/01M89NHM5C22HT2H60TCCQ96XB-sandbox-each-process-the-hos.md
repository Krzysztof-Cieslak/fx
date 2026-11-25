---
id: 01M89NHM5C22HT2H60TCCQ96XB
anchor: '@file'
created: 2026-11-25T15:35:17Z
norm: '1'
---

Sandbox each process the host starts for an agent (EnvHandle.Client), never envd itself: envd may be in the host's process, writes its own state, and serves processes with different policies (an agent's overrides).
