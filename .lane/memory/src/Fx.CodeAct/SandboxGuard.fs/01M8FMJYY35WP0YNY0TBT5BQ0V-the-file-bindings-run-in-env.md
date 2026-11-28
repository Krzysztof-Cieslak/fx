---
id: 01M8FMJYY35WP0YNY0TBT5BQ0V
anchor: '@file'
created: 2026-11-27T23:13:59Z
norm: '1'
sig: 4edb65de7bd06175
body_hash: eb9e7c0f89d01887
raw_hash: a95dda718669f82f
vouched: 2026-11-28T01:20:49Z
lines: 1-107
supersedes: 01M89NHM5VY33R6T4NBBHC0PY8
---

The file bindings run in envd, outside the sandbox: every host call is checked against the same policy, and the session's client resolves paths beneath the policy's writable roots (SandboxGuard.roots, EnvClient.beneath) so envd follows no link there; without that an agent could link to a denied path and read it through fs.read.
