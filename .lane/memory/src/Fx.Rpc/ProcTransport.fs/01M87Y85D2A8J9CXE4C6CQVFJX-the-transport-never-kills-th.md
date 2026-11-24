---
id: 01M87Y85D2A8J9CXE4C6CQVFJX
anchor: '@file'
created: 2026-11-24T23:28:55Z
norm: '1'
sig: b70dad81299f621a
body_hash: be641c7dcac114aa
raw_hash: 76401636491b2891
lines: 1-125
---

The transport never kills the process: closing ends the child's stdin and aborting stops reading; the owner (the process runtime) waits for the exit and kills it after a timeout.
