---
id: 01M885QT7AHS1T3XH79WZ3E04S
anchor: publish
created: 2026-11-25T01:39:48Z
norm: '1'
---

Roots are published (under the root lock) before an install that relies on them is inspected or activated, which is why the installer actor runs intents sequentially: a concurrent prune in another process must see every install a holder uses.
