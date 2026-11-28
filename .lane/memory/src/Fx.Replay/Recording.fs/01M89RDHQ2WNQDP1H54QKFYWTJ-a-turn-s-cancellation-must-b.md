---
id: 01M89RDHQ2WNQDP1H54QKFYWTJ
anchor: '@file'
created: 2026-11-25T16:25:29Z
norm: '1'
sig: 06e768d866788b80
body_hash: f4adf8265611426a
raw_hash: 08032d50b45d2efd
vouched: 2026-11-28T10:51:35Z
lines: 1-573
---

A turn's cancellation must be journaled (Turn.Cancelled) before the turn's own token is cancelled: replay fires it when everything recorded before it has happened, so in-flight calls must complete after the marker.
