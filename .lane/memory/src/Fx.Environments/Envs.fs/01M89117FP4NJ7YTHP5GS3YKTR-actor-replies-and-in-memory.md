---
id: 01M89117FP4NJ7YTHP5GS3YKTR
anchor: '@file'
created: 2026-11-25T09:36:48Z
norm: '1'
---

Actor replies and in-memory log wake-ups run on the appending thread: hand them on with `later` before reading the log or sending again; a subscription started inside an append spins forever and blocks the actor.
