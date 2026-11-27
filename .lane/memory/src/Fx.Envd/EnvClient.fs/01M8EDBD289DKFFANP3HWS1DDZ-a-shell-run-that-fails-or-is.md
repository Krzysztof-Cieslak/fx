---
id: 01M8EDBD289DKFFANP3HWS1DDZ
anchor: '@file'
created: 2026-11-27T11:48:17Z
norm: '1'
sig: 1c8cc5a01680bc71
body_hash: d87efbab982cd045
raw_hash: c256edf90f9b883c
vouched: 2026-11-27T23:13:56Z
lines: 1-854
---

A Shell.Run that fails or is cancelled must Abandon its Remote (reset the attachment, release the process), and spawn releases a started process whose attachment cannot be set up: nobody reads their output any more, so the channel (when envd has more than a window to send) and envd's process record would stay until the connection ends.
