---
id: 01M8868180W79K9JZPB5RSCGH7
anchor: '@file'
created: 2026-11-25T01:48:40Z
norm: '1'
---

Once a log has spilled, the spill file must hold the whole stream (offset 0 to the end): every later append is written to it, whatever the size of the memory window.
