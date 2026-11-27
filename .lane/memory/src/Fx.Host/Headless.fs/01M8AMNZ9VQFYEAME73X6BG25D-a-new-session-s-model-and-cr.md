---
id: 01M8AMNZ9VQFYEAME73X6BG25D
anchor: '@file'
created: 2026-11-26T00:39:25Z
norm: '1'
sig: aea2e01ee52e4c76
body_hash: 09d49ddaab13bdc2
raw_hash: d4754bceb0f76141
vouched: 2026-11-27T04:24:45Z
lines: 1-432
---

A new session's model and credentials are checked (Headless.modelReady) before HostSessions.create: session creation creates the environment, which installs components; a run that cannot call its model must fail before any download.
