---
id: 01M88E6REBK7WJPDBHGZGG6RST
anchor: '@file'
created: 2026-11-25T04:07:46Z
norm: '1'
---

Commands the host sends for a turn are keyed by the turn's start event ("<stream>#<seq>/step/<n>", "/request/<id>") and forks by the Forked event ("/fork"): never by the clock or random ids, so retries deduplicate.
