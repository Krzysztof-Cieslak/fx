---
id: 01M8H7G3SE52N9CH88MJDQHAAB
anchor: ContextAsks
created: 2026-11-28T14:03:43Z
norm: '1'
---

context.* bindings only ask; the turn records the asks as Pinned/Unpinned right after the cell's receipt (and drops stale asks when a turn starts). Never write context events from a binding: the context log's order must be the order the model saw.
