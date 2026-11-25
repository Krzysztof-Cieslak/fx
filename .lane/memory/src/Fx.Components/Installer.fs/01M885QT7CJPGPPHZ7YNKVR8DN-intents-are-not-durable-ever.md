---
id: 01M885QT7CJPGPPHZ7YNKVR8DN
anchor: recover
created: 2026-11-25T01:39:48Z
norm: '1'
---

Intents are not durable: every pipeline step must stay safe to run again from the journal (fetch reuses a verified archive, build clears staging first, inspect/fetch/build accept an install another installer finished).
