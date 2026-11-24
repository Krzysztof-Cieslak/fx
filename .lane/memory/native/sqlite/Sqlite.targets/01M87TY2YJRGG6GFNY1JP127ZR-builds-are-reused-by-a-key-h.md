---
id: 01M87TY2YJRGG6GFNY1JP127ZR
anchor: '@file'
created: 2026-11-24T22:30:59Z
norm: '1'
sig: 15a767339566cc27
body_hash: c77323e4f023a930
raw_hash: 4509c78bfb2a32fa
lines: 1-173
---

Builds are reused by a key hashing their inputs (pin, library.c, this file), never by timestamps: a CI cache of artifacts/native comes back with new mtimes and must still be used as is.
