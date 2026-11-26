---
id: 01M88768SXF081AE22Z85G5R1F
anchor: '@file'
created: 2026-11-25T02:05:10Z
norm: '1'
sig: f1ee8a2ef0996b8b
body_hash: 975002b17c5f6dc5
raw_hash: 7ac5c2486ca31c00
vouched: 2026-11-26T02:45:19Z
lines: 1-627
---

Fx.Rpc completes a call in the reader while notifications wait for the dispatcher, so run must wait for the CellResult.Notified count before returning, or output can arrive after the result.
