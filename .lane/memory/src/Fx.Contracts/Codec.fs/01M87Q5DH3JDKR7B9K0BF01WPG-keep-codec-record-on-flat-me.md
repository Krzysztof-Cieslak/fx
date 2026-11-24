---
id: 01M87Q5DH3JDKR7B9K0BF01WPG
anchor: '@file'
created: 2026-11-24T21:25:05Z
norm: '1'
---

Keep Codec.record on flat MergeSourcesN and Slot-erased RecordParts: a binary-only applicative builder nests one tuple per field, and the Native AOT publish fails with IL3054 (generic depth > 4 in FSharp.Core closures) at about 7 fields.
