---
id: 01M8FYSJ16ADY772R5F6AHQXGY
anchor: runFor
created: 2026-11-28T02:12:21Z
norm: '1'
---

Decide a command's timeout only by which finished first, its reads or the clock's delay (Eff.timeout on the environment's clock): both are journaled in order, so replay takes the same path; never the wall clock or whether a task has completed.
