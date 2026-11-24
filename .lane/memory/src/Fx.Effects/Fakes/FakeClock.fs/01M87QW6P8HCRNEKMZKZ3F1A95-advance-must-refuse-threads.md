---
id: 01M87QW6P8HCRNEKMZKZ3F1A95
anchor: '@file'
created: 2026-11-24T21:37:32Z
norm: '1'
---

Advance must refuse threads where RuntimeHelpers.TryEnsureSufficientExecutionStack() is false (e.g. the musl Native AOT main thread): continuations would not run inline and delays would fire out of order.
