---
id: 01M88E6RDBC3QES17EYGGN5B5P
anchor: '@file'
created: 2026-11-25T04:07:46Z
norm: '1'
sig: 5c855850a07edd97
body_hash: e6f3d547efe91c97
raw_hash: d3aa51fe28ebc53c
vouched: 2026-11-25T13:05:03Z
lines: 1-435
---

Interrupts must go through Actor.interrupt (SessionHost.send routes Command.Interrupt there): sent as a normal command the running turn would keep running after the session recorded its end.
