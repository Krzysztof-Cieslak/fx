---
id: 01M87S131HC6T04VK9E7KXV8J7
anchor: '@file'
created: 2026-11-24T21:57:41Z
norm: '1'
sig: a1d6bda1c13e616a
body_hash: 00be3385e0ed484f
raw_hash: dc169c6d4ed1c27a
lines: 1-562
---

Recovery intents start before the loop: once the loop waits, an intent that finishes at once would have its result handled inside startIntents and break IntentOrder.Sequential (tested by "sequential intents keep their order when recovery intents finish at once").
