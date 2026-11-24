---
id: 01M87S131K7RK6T5SX0RDFF64Q
anchor: '@file'
created: 2026-11-24T21:57:41Z
norm: '1'
sig: a1d6bda1c13e616a
body_hash: 00be3385e0ed484f
raw_hash: dc169c6d4ed1c27a
lines: 1-562
---

Result commands of intents are keyed "<event>/<intent>.<command>" and recovery ones "<stream>#<head>/recover/<intent>.<command>": deterministic, never from the clock or random ids, so a retried or replayed reaction deduplicates.
