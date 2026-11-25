---
id: 01M889AETA8K12TY0449SMVDBF
anchor: '@file'
created: 2026-11-25T02:42:25Z
norm: '1'
---

RuntimeSpec.launch must not depend on the machine that runs it: join and judge paths by session.Target's rules (no Path.Combine, no Path.IsPathRooted); the runtimes-launch scenario prints all eight targets and must read the same on every OS.
