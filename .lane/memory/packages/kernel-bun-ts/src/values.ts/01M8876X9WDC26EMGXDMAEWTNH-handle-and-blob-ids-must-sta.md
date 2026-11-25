---
id: 01M8876X9WDC26EMGXDMAEWTNH
anchor: '@file'
created: 2026-11-25T02:05:31Z
norm: '1'
sig: e3b0c44298fc1c14
body_hash: 8031c748a19c450d
raw_hash: 702b23db26f6e0c0
lines: 1-265
---

Handle and blob ids must stay in the kernel's WeakMaps, never on the objects cells see: that is what makes a reference unforgeable from a cell.
