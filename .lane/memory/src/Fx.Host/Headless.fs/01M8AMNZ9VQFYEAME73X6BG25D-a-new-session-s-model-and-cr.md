---
id: 01M8AMNZ9VQFYEAME73X6BG25D
anchor: '@file'
created: 2026-11-26T00:39:25Z
norm: '1'
sig: aea2e01ee52e4c76
body_hash: 294b10cf647a58c0
raw_hash: b54cbb1cef8f762c
vouched: 2026-11-27T12:12:18Z
lines: 1-432
---

A new session's model and credentials are checked (Headless.modelReady) before HostSessions.create: session creation creates the environment, which installs components; a run that cannot call its model must fail before any download.
