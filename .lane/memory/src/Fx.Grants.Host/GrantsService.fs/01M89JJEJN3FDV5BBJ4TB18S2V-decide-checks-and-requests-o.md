---
id: 01M89JJEJN3FDV5BBJ4TB18S2V
anchor: '@file'
created: 2026-11-25T14:43:18Z
norm: '1'
---

Decide checks and requests on the state as of the log's head (latest), never the actor's snapshot alone: await reads the log, so a caller can see an answer before the snapshot does.
