---
id: 01M885QMH8PWW1A0AFGBVR7PWJ
anchor: TaggedUnion
created: 2026-11-25T01:39:42Z
norm: '1'
---

Codec.union (curried match function) fails ILC with IL3054 beyond four cases on this base; TaggedUnion builds the same JSON from per-case record codecs. Replace it with T101b's Codec.union { } once that lands; the JSON must not change.
