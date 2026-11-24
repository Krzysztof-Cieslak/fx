---
id: 01M87MCGBV51GBAFBG5A9RVZ82
anchor: '@file'
created: 2026-11-24T20:36:32Z
norm: '1'
---

The canary must diverge by construction (Assembly.Location is empty only in a native binary). RuntimeFeature.IsDynamicCodeCompiled does not work: the RID-specific JIT build has the AOT feature switches and reports false as well.
