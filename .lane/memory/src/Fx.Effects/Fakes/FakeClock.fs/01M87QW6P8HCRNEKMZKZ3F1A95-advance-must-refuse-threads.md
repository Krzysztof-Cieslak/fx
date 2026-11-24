---
id: 01M87QW6P8HCRNEKMZKZ3F1A95
anchor: '@file'
created: 2026-11-24T21:37:32Z
norm: '1'
sig: 456aef46d0008e73
body_hash: 948c626e64e26755
raw_hash: 18681b0926452675
lines: 1-153
---

Advance must refuse threads where RuntimeHelpers.TryEnsureSufficientExecutionStack() is false (e.g. the musl Native AOT main thread): continuations would not run inline and delays would fire out of order.
