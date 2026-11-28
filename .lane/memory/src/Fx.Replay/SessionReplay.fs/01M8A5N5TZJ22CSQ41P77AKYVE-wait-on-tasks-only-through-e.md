---
id: 01M8A5N5TZJ22CSQ41P77AKYVE
anchor: '@file'
created: 2026-11-25T20:16:51Z
norm: '1'
sig: 06e768d866788b80
body_hash: e9b27044c5a5a90a
raw_hash: '9510345439085970'
vouched: 2026-11-28T01:44:22Z
lines: 1-802
---

Wait on tasks only through Eff.ofCancellableTask (WaitAsync): Eff.timeout's race waits for every child, so a non-cancellable wait turns a stall into a hang.
