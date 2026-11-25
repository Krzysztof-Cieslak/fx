---
id: 01M883Q4FXNMQPAMWVWBPPAHTD
anchor: '@file'
created: 2026-11-25T01:04:29Z
norm: '1'
sig: 0bd091687caad426
body_hash: c03b4478b18b871e
raw_hash: 51d013139acc070c
lines: 1-649
---

Keep the 24-case union and the 48-field record here: they are the only Native AOT proof that codecs have no size limit (IL3054). 48 fields nest the merged tuple 5 deep, past ILC's depth cutoff of 4; do not shrink below 46.
