---
id: 01M887XY81CDT165X3Y24A4KHG
anchor: '@file'
created: 2026-11-25T02:18:06Z
norm: '1'
---

LaunchSpec.untrustedOverrides is encoded only when non-empty, so that adding the field changed no existing manifest's hash (host config pins Manifest.hash). Keep new launch fields optional the same way.
