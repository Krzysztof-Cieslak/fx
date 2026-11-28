---
id: 01M8FSXJ7Y9MY4MN3SXPA020H7
anchor: CellCommands
created: 2026-11-28T00:47:09Z
norm: '1'
---

Never signal the kernel from inside the grants guard (the dropped `cell/hold`): replay answers the guard from one journal entry without running it. Kernels pause a cell's clock during host calls instead, and the host bounds shell.run by the cell's timeout, timed after the guard so that approval waits never count.
