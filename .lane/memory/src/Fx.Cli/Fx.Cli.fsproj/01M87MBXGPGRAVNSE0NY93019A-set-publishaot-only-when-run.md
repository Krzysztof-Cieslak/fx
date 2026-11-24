---
id: 01M87MBXGPGRAVNSE0NY93019A
anchor: '@file'
created: 2026-11-24T20:36:12Z
norm: '1'
---

Set PublishAot only when RuntimeIdentifier is non-empty: an unconditional PublishAot makes even the RID-less restore add the host RID, so the committed packages.lock.json would differ per OS and break --locked-mode restore elsewhere.
