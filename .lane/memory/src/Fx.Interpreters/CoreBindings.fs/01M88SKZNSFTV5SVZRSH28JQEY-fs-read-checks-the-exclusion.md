---
id: 01M88SKZNSFTV5SVZRSH28JQEY
anchor: '@file'
created: 2026-11-25T07:27:14Z
norm: '1'
sig: e9e84c84122a5e2d
body_hash: 0d4fd97479c466b7
raw_hash: 62197b42f270533e
vouched: 2026-11-28T13:01:56Z
lines: 1-703
---

fs.read checks the exclusions before touching the file: an excluded file's bytes must never reach the kernel, not even in an error.
