---
id: 01M88H6D8ZZ5VADBSCXN4XQF9D
anchor: '@file'
created: 2026-11-25T05:00:01Z
norm: '1'
sig: e9e84c84122a5e2d
body_hash: 8a06e2daa78eaee6
raw_hash: 08493c5062b67bed
lines: 1-201
---

Arguments decode only with the binding's codecs through Value.decode, so references never decode into data (spec §6.6, §18); a binding that throws is a defect, expected failures are HostCallError.
