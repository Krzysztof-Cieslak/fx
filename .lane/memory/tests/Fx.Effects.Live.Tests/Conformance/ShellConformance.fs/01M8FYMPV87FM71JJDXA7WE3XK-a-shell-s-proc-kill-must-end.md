---
id: 01M8FYMPV87FM71JJDXA7WE3XK
anchor: '@file'
created: 2026-11-28T02:09:42Z
norm: '1'
sig: 51222de98234c0e2
body_hash: 65b9e692e89fef75
raw_hash: 8de348477f71130f
lines: 1-418
---

A Shell's Proc.Kill must end the process's whole tree (children still in it) so its output ends: shell.run reads a timed-out command's output to its end after killing it.
