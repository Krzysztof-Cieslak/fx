---
id: 01M8D20G1485673BCTFF12EXH0
anchor: '@file'
created: 2026-11-26T23:10:50Z
norm: '1'
sig: 918e4a8de87248e2
body_hash: e4a050b42b172f9f
raw_hash: c59a3a0cf7e5ca3f
vouched: 2026-11-27T00:11:06Z
lines: 1-1315
supersedes: 01M8C7TQAYKEC7QJ2N6Q8TJFJT
---

Every screen (View.main, View.start) paints its rows with the theme (Fg/Bg Default take its text and background) and View.render lowers the frame to the terminal's depth last (View.view is its frame): spans may use any Color; never lower anywhere else.
