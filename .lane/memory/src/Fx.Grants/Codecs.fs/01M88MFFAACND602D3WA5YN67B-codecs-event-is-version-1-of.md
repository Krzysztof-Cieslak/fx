---
id: 01M88MFFAACND602D3WA5YN67B
anchor: '@file'
created: 2026-11-25T05:57:23Z
norm: '1'
sig: 16d4050a8067cd99
body_hash: b27657a2b899ae1e
raw_hash: c6bb8036180ebbfc
lines: 1-794
---

Codecs.event is version 1 of the grants log's event schema: committed events are never rewritten. Change it only through a new schema version and an upcaster; tests/Fx.Grants.Tests/golden/events-v1.jsonl must keep reading.
