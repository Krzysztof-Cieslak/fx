---
id: 01M8A5N5TZJ22CSQ41P77AKYVE
anchor: '@file'
created: 2026-11-25T20:16:51Z
norm: '1'
sig: 06e768d866788b80
body_hash: 8e3cb03f4a3e8ee8
raw_hash: 65fb8030ca13365d
vouched: 2026-11-28T10:51:35Z
lines: 1-802
---

Wait on tasks only through Eff.ofCancellableTask (WaitAsync): Eff.timeout's race waits for every child, so a non-cancellable wait turns a stall into a hang.
