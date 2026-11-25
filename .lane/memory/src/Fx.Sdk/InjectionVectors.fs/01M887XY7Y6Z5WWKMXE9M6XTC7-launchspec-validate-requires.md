---
id: 01M887XY7Y6Z5WWKMXE9M6XTC7
anchor: '@file'
created: 2026-11-25T02:18:06Z
norm: '1'
---

LaunchSpec.validate requires every pattern listed here in a runtime's EnvDeny, and Manifest.deniedEnvironment is this list: adding a vector invalidates every runtime spec that does not deny it, so update the built-ins' EnvDeny in the same change.
