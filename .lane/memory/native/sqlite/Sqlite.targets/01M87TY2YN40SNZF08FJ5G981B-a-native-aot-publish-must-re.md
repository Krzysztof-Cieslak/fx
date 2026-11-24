---
id: 01M87TY2YN40SNZF08FJ5G981B
anchor: '@file'
created: 2026-11-24T22:30:59Z
norm: '1'
sig: 15a767339566cc27
body_hash: c77323e4f023a930
raw_hash: 4509c78bfb2a32fa
lines: 1-173
---

A Native AOT publish must remove the shared library from ResolvedFileToPublish: otherwise it lands next to the binary (checked: it did), and the binary is no longer a single file.
