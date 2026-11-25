---
id: 01M894EKTQMH9981CP54WTG0PS
anchor: '@file'
created: 2026-11-25T10:36:32Z
norm: '1'
---

Never set SetLastError = true on these imports: runtime marshalling is disabled for the assembly, so such a call throws; read lastError () right after the failing call instead.
