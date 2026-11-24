---
id: 01M87VJ1RZKCXV3SE9Z6YQRZTN
anchor: '@file'
created: 2026-11-24T22:41:53Z
norm: '1'
sig: b70dad81299f621a
body_hash: 501e9bc39ba69d27
raw_hash: 796a1bbad7731b56
lines: 1-301
---

Binary stream frames start with a zero byte, which JSON text never contains; this keeps plain NDJSON peers (the Bun kernel, scripts) compatible. Text frames must never contain a raw line break.
