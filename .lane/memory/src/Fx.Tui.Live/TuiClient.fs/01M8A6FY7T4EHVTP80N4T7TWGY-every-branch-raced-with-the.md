---
id: 01M8A6FY7T4EHVTP80N4T7TWGY
anchor: '@file'
created: 2026-11-25T20:31:27Z
norm: '1'
sig: 41275802be33b25c
body_hash: 8f5459fa0b2e7fd9
raw_hash: 43be05527b190bc1
vouched: 2026-11-27T12:12:19Z
lines: 1-273
---

Every branch raced with the loop must honour the scope's cancellation (peer.Completion does not): Eff.race waits for all of them, so one that ignores it keeps fx from exiting.
