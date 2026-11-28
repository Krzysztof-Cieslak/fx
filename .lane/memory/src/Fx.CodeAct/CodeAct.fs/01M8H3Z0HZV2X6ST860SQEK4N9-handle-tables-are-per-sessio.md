---
id: 01M8H3Z0HZV2X6ST860SQEK4N9
anchor: rangesOf
created: 2026-11-28T13:01:57Z
norm: '1'
---

Handle tables are per session (ranges keyed by session): a handle must never resolve in another session, so never share one table across sessions or make it a singleton.
