---
id: 01M89NHM5VY33R6T4NBBHC0PY8
anchor: '@file'
created: 2026-11-25T15:35:17Z
norm: '1'
---

The file bindings run in envd, outside the sandbox: every host call is checked against the same policy, and paths through symbolic links inside writable roots are refused, or an agent could link to a denied path and read it through fs.read.
