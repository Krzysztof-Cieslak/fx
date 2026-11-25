---
id: 01M8AFZYY59P7FGVJC54AGDCKP
anchor: '@file'
created: 2026-11-25T23:17:30Z
norm: '1'
---

Interpreter.Launches must give exactly what each launch starts from: replay rebuilds kernels from the journaled LaunchInfo, so a launch input outside InterpreterSession breaks fx replay.
