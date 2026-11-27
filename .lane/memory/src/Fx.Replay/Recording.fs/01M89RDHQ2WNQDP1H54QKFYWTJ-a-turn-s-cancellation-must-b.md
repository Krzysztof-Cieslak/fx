---
id: 01M89RDHQ2WNQDP1H54QKFYWTJ
anchor: '@file'
created: 2026-11-25T16:25:29Z
norm: '1'
sig: 06e768d866788b80
body_hash: ffa0ee2ac4904e57
raw_hash: 6db1d7446e7db6e3
vouched: 2026-11-27T04:24:45Z
lines: 1-573
---

A turn's cancellation must be journaled (Turn.Cancelled) before the turn's own token is cancelled: replay fires it when everything recorded before it has happened, so in-flight calls must complete after the marker.
