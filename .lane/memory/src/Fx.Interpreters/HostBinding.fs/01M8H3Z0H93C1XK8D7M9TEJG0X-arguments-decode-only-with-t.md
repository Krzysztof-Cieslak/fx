---
id: 01M8H3Z0H93C1XK8D7M9TEJG0X
anchor: '@file'
created: 2026-11-28T13:01:57Z
norm: '1'
sig: e9e84c84122a5e2d
body_hash: a159f09fe1d4ba1c
raw_hash: fffa473a34f8133a
lines: 1-306
supersedes: 01M88H6D8ZZ5VADBSCXN4XQF9D
---

Arguments decode only with the binding's codecs through Value.decode, so references never decode into data (spec §6.6, §18); the one exception is Arg.valueOrHandle, which resolves a handle only through the kind's own session table and declared type. A binding that throws is a defect, expected failures are HostCallError.
