---
id: 01M8C6YVHQ6QG1ESXV9GWB0TTQ
anchor: '@file'
created: 2026-11-26T15:18:05Z
norm: '1'
sig: 41275802be33b25c
body_hash: 212d29e4ac371a54
raw_hash: dffccb53416da9c4
lines: 1-386
---

Every branch of an Eff.race must end when its scope is cancelled (Eff.race waits for all of them): the switch waiter awaits Switches.Next with WaitAsync on the scope, or quitting hangs.
