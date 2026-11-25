---
id: 01M89117FP4NJ7YTHP5GS3YKTR
anchor: '@file'
created: 2026-11-25T09:36:48Z
norm: '1'
sig: db88ec098994fb1c
body_hash: 375c476a68382aaf
raw_hash: c7c8b93de7b1f529
vouched: 2026-11-25T22:34:03Z
lines: 1-1040
---

Actor replies and in-memory log wake-ups run on the appending thread: hand them on with `later` before reading the log or sending again; a subscription started inside an append spins forever and blocks the actor.
