---
id: 01M8FMJYY35WP0YNY0TBT5BQ0V
anchor: '@file'
created: 2026-11-27T23:13:59Z
norm: '1'
sig: 4edb65de7bd06175
body_hash: fc1d6289b644b674
raw_hash: e4ad94ead7de6d55
vouched: 2026-11-28T18:33:29Z
lines: 1-107
supersedes: 01M89NHM5VY33R6T4NBBHC0PY8
---

The file bindings run in envd, outside the sandbox: every host call is checked against the same policy, and the session's client resolves paths beneath the policy's writable roots (SandboxGuard.roots, EnvClient.beneath) so envd follows no link there; without that an agent could link to a denied path and read it through fs.read.
