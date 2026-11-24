---
id: 01M87S131K7RK6T5SX0RDFF64Q
anchor: '@file'
created: 2026-11-24T21:57:41Z
norm: '1'
---

Result commands of intents are keyed "<event>/<intent>.<command>" and recovery ones "<stream>#<head>/recover/<intent>.<command>": deterministic, never from the clock or random ids, so a retried or replayed reaction deduplicates.
