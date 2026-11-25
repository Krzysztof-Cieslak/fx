---
id: 01M88768SXF081AE22Z85G5R1F
anchor: '@file'
created: 2026-11-25T02:05:10Z
norm: '1'
sig: f1ee8a2ef0996b8b
body_hash: 5ca1980477c14e40
raw_hash: 12fe1f4f0c39bfb7
lines: 1-627
---

Fx.Rpc completes a call in the reader while notifications wait for the dispatcher, so run must wait for the CellResult.Notified count before returning, or output can arrive after the result.
