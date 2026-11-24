---
id: 01M87S131K7RK6T5SX0RDFF64Q
anchor: '@file'
created: 2026-11-24T21:57:41Z
norm: '1'
sig: a1d6bda1c13e616a
body_hash: d8f79507e3f2dbd1
raw_hash: eb887db276a9cb3d
vouched: 2026-11-24T23:48:02Z
lines: 1-562
---

Result commands of intents are keyed "<event>/<intent>.<command>" and recovery ones "<stream>#<head>/recover/<intent>.<command>": deterministic, never from the clock or random ids, so a retried or replayed reaction deduplicates.
