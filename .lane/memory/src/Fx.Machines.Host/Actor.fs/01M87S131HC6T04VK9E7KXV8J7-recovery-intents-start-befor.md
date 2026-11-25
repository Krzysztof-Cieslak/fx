---
id: 01M87S131HC6T04VK9E7KXV8J7
anchor: '@file'
created: 2026-11-24T21:57:41Z
norm: '1'
sig: a1d6bda1c13e616a
body_hash: a8a544b09f47c407
raw_hash: 6d563f7d2b6e6c14
vouched: 2026-11-25T04:16:00Z
lines: 1-562
---

Recovery intents start before the loop: once the loop waits, an intent that finishes at once would have its result handled inside startIntents and break IntentOrder.Sequential (tested by "sequential intents keep their order when recovery intents finish at once").
