---
id: 01M87Q51ZWMA4HAPHSC0KFEGZ9
anchor: '@file'
created: 2026-11-24T21:24:53Z
norm: '1'
sig: 957252fadfbeaef4
body_hash: 38c0bb587dbd9f67
raw_hash: ce028c05f70ab825
lines: 1-454
---

Effects driven by a FakeClock must stay on the advancing thread (no Task.Yield or real I/O): a thread-pool hop races with the next Advance and makes the output nondeterministic.
