---
id: 01M8HPXA1ZP2E371Y64081K2VT
anchor: fsSearch
created: 2026-11-28T18:33:04Z
norm: '1'
---

fs/search must resolve beneath the request's roots: repo.grep and repo.files call it, and without beneath a search rooted at a link would walk outside the workspace.
