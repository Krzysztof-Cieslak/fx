---
id: 01M89NHM5VY33R6T4NBBHC0PY8
anchor: '@file'
created: 2026-11-25T15:35:17Z
norm: '1'
sig: 4edb65de7bd06175
body_hash: 2aca591b3e961537
raw_hash: fa7ff5c900221ad9
lines: 1-134
---

The file bindings run in envd, outside the sandbox: every host call is checked against the same policy, and paths through symbolic links inside writable roots are refused, or an agent could link to a denied path and read it through fs.read.
