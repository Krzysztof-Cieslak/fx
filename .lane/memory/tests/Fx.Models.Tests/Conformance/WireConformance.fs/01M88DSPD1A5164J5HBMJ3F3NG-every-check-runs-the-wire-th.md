---
id: 01M88DSPD1A5164J5HBMJ3F3NG
anchor: '@file'
created: 2026-11-25T04:00:38Z
norm: '1'
sig: c93a3d54f38530d3
body_hash: a0dc0699ade0a76d
raw_hash: a9a4a624cb3bbc4b
lines: 1-545
---

Every check runs the wire through ModelAccess (never wire.Stream directly) and a CassetteHttp: the suite must exercise the same path, egress included, that production calls take.
