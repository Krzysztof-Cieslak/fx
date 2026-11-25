---
id: 01M8AFZYY59P7FGVJC54AGDCKP
anchor: '@file'
created: 2026-11-25T23:17:30Z
norm: '1'
sig: 4edb65de7bd06175
body_hash: 3afecb4a7af5f8d0
raw_hash: a75c61bdc991d7e0
lines: 1-516
---

Interpreter.Launches must give exactly what each launch starts from: replay rebuilds kernels from the journaled LaunchInfo, so a launch input outside InterpreterSession breaks fx replay.
