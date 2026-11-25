---
id: 01M88Q82FYQGZ2TC1Q1ZGK2GRP
anchor: '@file'
created: 2026-11-25T06:45:47Z
norm: '1'
sig: 3d650a0c56fd71ae
body_hash: 1ea4ee498cee2ae5
raw_hash: 2e276173715d3c76
lines: 1-159
---

Record an event as sent before writing it: the client may parse it before the write's flush returns, and a stop then cancels the write; the ack handshake keeps the label from running ahead.
