---
id: 01M87VJ1RZKCXV3SE9Z6YQRZTN
anchor: '@file'
created: 2026-11-24T22:41:53Z
norm: '1'
---

Binary stream frames start with a zero byte, which JSON text never contains; this keeps plain NDJSON peers (the Bun kernel, scripts) compatible. Text frames must never contain a raw line break.
