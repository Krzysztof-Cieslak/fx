---
id: 01M892XBKD6VJ64KHV3SV84F22
anchor: '@file'
created: 2026-11-25T10:09:38Z
norm: '1'
---

CoreFoundation's Boolean is an unsigned char: declare it as byte, never int (only the low byte of the return register is defined).
