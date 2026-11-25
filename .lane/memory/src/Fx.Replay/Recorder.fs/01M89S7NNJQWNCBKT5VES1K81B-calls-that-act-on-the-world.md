---
id: 01M89S7NNJQWNCBKT5VES1K81B
anchor: '@file'
created: 2026-11-25T16:39:45Z
norm: '1'
sig: 06e768d866788b80
body_hash: 55e841dbb306fb12
raw_hash: 546cca1d40e0203c
vouched: 2026-11-25T23:30:35Z
lines: 1-388
---

Calls that act on the world (Proc.Write, CloseStdin, Kill) must be numbered when they start (StartActing/RunActing), calls that bring something from the world when they complete: replay gates reads on everything numbered before them.
