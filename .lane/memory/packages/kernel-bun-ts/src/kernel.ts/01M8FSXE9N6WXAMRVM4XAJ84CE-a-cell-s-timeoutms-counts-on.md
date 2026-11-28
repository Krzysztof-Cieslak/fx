---
id: 01M8FSXE9N6WXAMRVM4XAJ84CE
anchor: class CellClock
created: 2026-11-28T00:47:05Z
norm: '1'
---

A cell's timeoutMs counts only its own time: the clock pauses while any of the cell's host calls is in flight (a person may take long to approve one) and resumes with what was left; cell/cancel and interrupts still stop the cell at once. The host bounds shell.run itself (CellCommands).
