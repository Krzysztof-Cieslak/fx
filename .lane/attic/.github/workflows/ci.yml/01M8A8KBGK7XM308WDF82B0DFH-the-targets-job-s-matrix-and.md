---
id: 01M8A8KBGK7XM308WDF82B0DFH
anchor: '@file'
created: 2026-11-25T21:08:17Z
norm: '1'
sig: 8e7430f31889b761
body_hash: 6edbb096b79ef51c
raw_hash: 20ef222f9126be06
lines: 1-519
supersedes: 01M89EYWVARH99SQ94G6BPKH5Q
---

The targets job's matrix and every area step's condition come from the plan job's outputs: add targets and areas to scripts/ci-plan.ts, not to the workflow. The os job's three runners are the exception: fixed here and equal to OS_JOBS in scripts/ci-plan.ts (its tests check it).
