---
id: 01M8A5N5TZJ22CSQ41P77AKYVE
anchor: '@file'
created: 2026-11-25T20:16:51Z
norm: '1'
sig: 06e768d866788b80
body_hash: e7fe562c2b47ef3c
raw_hash: 4d4b7020c3c65439
vouched: 2026-11-27T12:12:19Z
lines: 1-802
---

Wait on tasks only through Eff.ofCancellableTask (WaitAsync): Eff.timeout's race waits for every child, so a non-cancellable wait turns a stall into a hang.
