---
id: 01M890MAJNDMHCATVEHQPMEMVJ
anchor: '@file'
created: 2026-11-25T09:29:45Z
norm: '1'
sig: 538d9858d47cb6bf
body_hash: 696cdec51718bb99
raw_hash: 65dd485bf11ea320
lines: 1-772
---

A pattern that cannot be used (blank, a negation, no path, a reversed range, too many alternatives) is an error, never skipped: an exclusion silently ignored would leak what it was meant to withhold (moved here from Fx.Prompt's matcher, T126).
