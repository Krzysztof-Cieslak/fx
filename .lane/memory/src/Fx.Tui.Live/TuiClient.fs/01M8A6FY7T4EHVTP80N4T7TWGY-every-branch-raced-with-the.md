---
id: 01M8A6FY7T4EHVTP80N4T7TWGY
anchor: '@file'
created: 2026-11-25T20:31:27Z
norm: '1'
sig: 41275802be33b25c
body_hash: 93dceaa9b24b984a
raw_hash: 198351b18635af81
lines: 1-273
---

Every branch raced with the loop must honour the scope's cancellation (peer.Completion does not): Eff.race waits for all of them, so one that ignores it keeps fx from exiting.
