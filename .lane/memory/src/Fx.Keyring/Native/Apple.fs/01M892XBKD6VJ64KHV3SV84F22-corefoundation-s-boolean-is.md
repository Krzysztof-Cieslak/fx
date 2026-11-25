---
id: 01M892XBKD6VJ64KHV3SV84F22
anchor: '@file'
created: 2026-11-25T10:09:38Z
norm: '1'
sig: 04da2d6b3cf5b650
body_hash: 866667893c7a030f
raw_hash: e87f34d8585df551
lines: 1-312
---

CoreFoundation's Boolean is an unsigned char: declare it as byte, never int (only the low byte of the return register is defined).
