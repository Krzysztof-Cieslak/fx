---
id: 01M87S131K7RK6T5SX0RDFF64Q
anchor: '@file'
created: 2026-11-24T21:57:41Z
norm: '1'
sig: a1d6bda1c13e616a
body_hash: a8a544b09f47c407
raw_hash: 6d563f7d2b6e6c14
vouched: 2026-11-25T04:16:00Z
lines: 1-562
---

Result commands of intents are keyed "<event>/<intent>.<command>" and recovery ones "<stream>#<head>/recover/<intent>.<command>": deterministic, never from the clock or random ids, so a retried or replayed reaction deduplicates.
