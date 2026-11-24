---
id: 01M87MBXGPPP2C30H8W0S1SR6N
anchor: '@file'
created: 2026-11-24T20:36:12Z
norm: '1'
sig: 15a767339566cc27
body_hash: ff4c1b39dd5ed205
raw_hash: c139ee8e2740acb4
vouched: 2026-11-24T22:40:41Z
lines: 1-11
---

FxRequireRuntimeIdentifierForNativeAot must stay: FxNativeAot projects set PublishAot only for RID-specific builds, so a publish without -r would silently produce a JIT build.
