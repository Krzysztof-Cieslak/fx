---
id: 01M8876X9WDC26EMGXDMAEWTNH
anchor: '@file'
created: 2026-11-25T02:05:31Z
norm: '1'
sig: e3b0c44298fc1c14
body_hash: 68c158d9dca9c2be
raw_hash: fd0d702948282e12
vouched: 2026-11-25T05:00:54Z
lines: 1-265
---

Handle and blob ids must stay in the kernel's WeakMaps, never on the objects cells see: that is what makes a reference unforgeable from a cell.
