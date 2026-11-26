---
id: 01M8B33598YD1MH4GZHYQH2HK8
anchor: '@file'
created: 2026-11-26T04:51:17Z
norm: '1'
sig: e1a178df2be44477
body_hash: d09cf9c87b43d91b
raw_hash: 0236057255797b5f
lines: 1-407
---

In resumable code, pass the machine (&sm) only to a resumable code's Invoke and the method builder's generic Start/AwaitUnsafeOnCompleted; give helpers data (EffMachine.IsError sm.Data.Result). A static machine is a generated struct: a method that gets it as ResumableStateMachine reads the wrong offsets, and Debug builds (F# 11) call inline helpers instead of inlining them.
