---
id: 01M87PZYG2K9KAB4A9WSDANB1V
anchor: '@file'
created: 2026-11-24T21:22:06Z
norm: '1'
---

Structured combinators start children inline with Eff.run (never Task.Run) so fake-clock delays register in a deterministic order, and complete only after every child finished. Production effects must honour Scope.Cancellation or race/timeout wait for them.
