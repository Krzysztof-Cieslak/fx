---
id: 01M8AAVYN2F50Z7RT6BY4QN4H9
anchor: '@file'
created: 2026-11-25T21:47:55Z
norm: '1'
sig: 1c8cc5a01680bc71
body_hash: 60532a9f5c23b6b7
raw_hash: 29bcb7444debf984
lines: 1-141
---

Complete (a closed watch) must drop pending changes and make every Next, including one waiting or debouncing, return None: OS watchers report changes after they are stopped (Windows even after Dispose), and a closed watch must deliver nothing.
