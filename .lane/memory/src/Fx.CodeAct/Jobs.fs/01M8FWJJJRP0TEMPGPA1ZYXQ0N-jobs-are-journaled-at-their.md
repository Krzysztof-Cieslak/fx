---
id: 01M8FWJJJRP0TEMPGPA1ZYXQ0N
anchor: LiveJobs
created: 2026-11-28T01:33:35Z
norm: '1'
---

Jobs are journaled at their level (Hands.Jobs, wrapped like Hands.Guard) and run on the hands' unrecorded shell: recording their processes' reads too would make replay expect them, and in-memory state read by jobs.status would race with them.
