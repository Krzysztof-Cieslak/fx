---
id: 01M87TM3DE2QQWM6TW85BTG75P
anchor: '@file'
created: 2026-11-24T22:25:32Z
norm: '1'
---

ioctl is variadic: Apple arm64 passes variadic arguments on the stack, so its third argument goes through ioctlStack (padding x2..x7). Never declare a variadic argument as a fixed P/Invoke parameter.
