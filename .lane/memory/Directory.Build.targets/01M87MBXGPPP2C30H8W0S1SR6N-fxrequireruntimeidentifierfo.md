---
id: 01M87MBXGPPP2C30H8W0S1SR6N
anchor: '@file'
created: 2026-11-24T20:36:12Z
norm: '1'
---

FxRequireRuntimeIdentifierForNativeAot must stay: FxNativeAot projects set PublishAot only for RID-specific builds, so a publish without -r would silently produce a JIT build.
