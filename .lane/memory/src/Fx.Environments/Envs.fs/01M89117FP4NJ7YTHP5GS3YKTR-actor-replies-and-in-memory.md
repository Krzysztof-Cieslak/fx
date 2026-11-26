---
id: 01M89117FP4NJ7YTHP5GS3YKTR
anchor: '@file'
created: 2026-11-25T09:36:48Z
norm: '1'
sig: db88ec098994fb1c
body_hash: 52401d8008b3b369
raw_hash: fe23c4cfd76a12bc
vouched: 2026-11-26T23:26:19Z
lines: 1-1040
---

Actor replies and in-memory log wake-ups run on the appending thread: hand them on with `later` before reading the log or sending again; a subscription started inside an append spins forever and blocks the actor.
