---
id: 01M87MCGBV51GBAFBG5A9RVZ82
anchor: '@file'
created: 2026-11-24T20:36:32Z
norm: '1'
sig: e8f5e0008c092a6f
body_hash: b8491a0962a06096
raw_hash: b9ad8ffbb71f91f8
lines: 1-21
---

The canary must diverge by construction (Assembly.Location is empty only in a native binary). RuntimeFeature.IsDynamicCodeCompiled does not work: the RID-specific JIT build has the AOT feature switches and reports false as well.
