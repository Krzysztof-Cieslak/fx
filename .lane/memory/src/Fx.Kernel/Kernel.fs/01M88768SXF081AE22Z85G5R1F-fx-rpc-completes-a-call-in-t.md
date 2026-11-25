---
id: 01M88768SXF081AE22Z85G5R1F
anchor: '@file'
created: 2026-11-25T02:05:10Z
norm: '1'
---

Fx.Rpc completes a call in the reader while notifications wait for the dispatcher, so run must wait for the CellResult.Notified count before returning, or output can arrive after the result.
