---
id: 01M87MBXGPGRAVNSE0NY93019A
anchor: '@file'
created: 2026-11-24T20:36:12Z
norm: '1'
sig: 81590f63c7cc7524
body_hash: b0be5459091bad33
raw_hash: 6d60f9790e03c8aa
vouched: 2026-11-24T22:40:41Z
lines: 1-24
---

Set PublishAot only when RuntimeIdentifier is non-empty: an unconditional PublishAot makes even the RID-less restore add the host RID, so the committed packages.lock.json would differ per OS and break --locked-mode restore elsewhere.
