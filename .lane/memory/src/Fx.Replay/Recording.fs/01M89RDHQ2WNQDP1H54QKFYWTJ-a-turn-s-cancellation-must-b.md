---
id: 01M89RDHQ2WNQDP1H54QKFYWTJ
anchor: '@file'
created: 2026-11-25T16:25:29Z
norm: '1'
sig: 06e768d866788b80
body_hash: 499a0aa3374c1d26
raw_hash: 7bdf6aeaef445447
vouched: 2026-11-28T01:44:22Z
lines: 1-573
---

A turn's cancellation must be journaled (Turn.Cancelled) before the turn's own token is cancelled: replay fires it when everything recorded before it has happened, so in-flight calls must complete after the marker.
