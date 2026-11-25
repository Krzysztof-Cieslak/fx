---
id: 01M8A8KBGK7XM308WDF82B0DFH
anchor: '@file'
created: 2026-11-25T21:08:17Z
norm: '1'
supersedes: 01M89EYWVARH99SQ94G6BPKH5Q
---

The targets job's matrix and every area step's condition come from the plan job's outputs: add targets and areas to scripts/ci-plan.ts, not to the workflow. The os job's three runners are the exception: fixed here and equal to OS_JOBS in scripts/ci-plan.ts (its tests check it).
