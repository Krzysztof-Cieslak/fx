---
id: 01M8A6FY7T4EHVTP80N4T7TWGY
anchor: '@file'
created: 2026-11-25T20:31:27Z
norm: '1'
sig: 41275802be33b25c
body_hash: 173aa1b0a6498575
raw_hash: 66de2246911d3b80
vouched: 2026-11-27T01:06:20Z
lines: 1-273
---

Every branch raced with the loop must honour the scope's cancellation (peer.Completion does not): Eff.race waits for all of them, so one that ignores it keeps fx from exiting.
