---
id: 01M89117FP4NJ7YTHP5GS3YKTR
anchor: '@file'
created: 2026-11-25T09:36:48Z
norm: '1'
sig: db88ec098994fb1c
body_hash: 6f09194aab0b8cf6
raw_hash: b439f3aea2ec1a2b
vouched: 2026-11-27T02:59:27Z
lines: 1-1040
---

Actor replies and in-memory log wake-ups run on the appending thread: hand them on with `later` before reading the log or sending again; a subscription started inside an append spins forever and blocks the actor.
