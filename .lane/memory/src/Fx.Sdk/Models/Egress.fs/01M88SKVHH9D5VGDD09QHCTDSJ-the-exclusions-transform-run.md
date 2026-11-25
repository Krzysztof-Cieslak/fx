---
id: 01M88SKVHH9D5VGDD09QHCTDSJ
anchor: '@file'
created: 2026-11-25T07:27:10Z
norm: '1'
---

The exclusions transform runs first (Priority -1000) and the gate re-checks the final request: a later transform may put excluded content back, and only the gate sees what would leave.
