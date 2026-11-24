---
id: 01M87TM3DE2QQWM6TW85BTG75P
anchor: '@file'
created: 2026-11-24T22:25:32Z
norm: '1'
sig: 5a88600e5b228adf
body_hash: 34eb1660fe240d91
raw_hash: cbfa9ece94c3a3d0
lines: 1-198
---

ioctl is variadic: Apple arm64 passes variadic arguments on the stack, so its third argument goes through ioctlStack (padding x2..x7). Never declare a variadic argument as a fixed P/Invoke parameter.
