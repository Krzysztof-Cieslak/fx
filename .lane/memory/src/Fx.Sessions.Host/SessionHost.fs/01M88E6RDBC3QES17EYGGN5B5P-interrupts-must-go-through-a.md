---
id: 01M88E6RDBC3QES17EYGGN5B5P
anchor: '@file'
created: 2026-11-25T04:07:46Z
norm: '1'
sig: 5c855850a07edd97
body_hash: 829125fc15db1d46
raw_hash: 10a698f98b0296f1
lines: 1-435
---

Interrupts must go through Actor.interrupt (SessionHost.send routes Command.Interrupt there): sent as a normal command the running turn would keep running after the session recorded its end.
