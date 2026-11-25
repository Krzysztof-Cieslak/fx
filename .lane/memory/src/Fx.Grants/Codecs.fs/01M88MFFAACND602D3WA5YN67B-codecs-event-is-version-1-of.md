---
id: 01M88MFFAACND602D3WA5YN67B
anchor: '@file'
created: 2026-11-25T05:57:23Z
norm: '1'
---

Codecs.event is version 1 of the grants log's event schema: committed events are never rewritten. Change it only through a new schema version and an upcaster; tests/Fx.Grants.Tests/golden/events-v1.jsonl must keep reading.
