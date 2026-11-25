---
id: 01M87PZYG2K9KAB4A9WSDANB1V
anchor: '@file'
created: 2026-11-24T21:22:06Z
norm: '1'
sig: e1a178df2be44477
body_hash: 79eae2d7ef1eb572
raw_hash: 67f421ceb323f6ab
vouched: 2026-11-25T23:30:35Z
lines: 1-233
---

Structured combinators start children inline with Eff.run (never Task.Run) so fake-clock delays register in a deterministic order, and complete only after every child finished. Production effects must honour Scope.Cancellation or race/timeout wait for them.
