---
id: 01M89NH5D21MKKE31KHJ8FHSJN
anchor: '@file'
created: 2026-11-25T15:35:02Z
norm: '1'
sig: 49d5b330b4dae107
body_hash: 22d9d3b78f55c19f
raw_hash: 05e50b01392cfc04
lines: 1-737
---

SandboxPolicy.tighten only takes access away (read-only, no network, more protected or denied paths); a project, spec or agent must never be able to widen a sandbox, only the host's privileged variants can.
