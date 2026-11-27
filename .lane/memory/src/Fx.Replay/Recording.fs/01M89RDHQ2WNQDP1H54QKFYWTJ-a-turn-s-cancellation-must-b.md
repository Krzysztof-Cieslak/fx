---
id: 01M89RDHQ2WNQDP1H54QKFYWTJ
anchor: '@file'
created: 2026-11-25T16:25:29Z
norm: '1'
sig: 06e768d866788b80
body_hash: a7357492733abbe3
raw_hash: be5eba9434cab16f
vouched: 2026-11-27T02:59:28Z
lines: 1-573
---

A turn's cancellation must be journaled (Turn.Cancelled) before the turn's own token is cancelled: replay fires it when everything recorded before it has happened, so in-flight calls must complete after the marker.
