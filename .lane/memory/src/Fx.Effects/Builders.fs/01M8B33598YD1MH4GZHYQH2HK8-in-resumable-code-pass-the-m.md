---
id: 01M8B33598YD1MH4GZHYQH2HK8
anchor: '@file'
created: 2026-11-26T04:51:17Z
norm: '1'
---

In resumable code, pass the machine (&sm) only to a resumable code's Invoke and the method builder's generic Start/AwaitUnsafeOnCompleted; give helpers data (EffMachine.IsError sm.Data.Result). A static machine is a generated struct: a method that gets it as ResumableStateMachine reads the wrong offsets, and Debug builds (F# 11) call inline helpers instead of inlining them.
