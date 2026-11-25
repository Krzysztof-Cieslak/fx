---
id: 01M8AG02A0MTF1QJCR8NT1HZ61
anchor: '@file'
created: 2026-11-25T23:17:33Z
norm: '1'
sig: aea2e01ee52e4c76
body_hash: 4e248dff75c1c499
raw_hash: 5256226e4c6328c7
lines: 1-255
---

fx replay opens the stores only under the data directory's lock and never writes them: replay writes go to its in-memory layers.
