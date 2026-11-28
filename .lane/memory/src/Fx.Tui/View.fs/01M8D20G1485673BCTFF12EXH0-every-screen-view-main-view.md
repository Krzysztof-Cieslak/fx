---
id: 01M8D20G1485673BCTFF12EXH0
anchor: '@file'
created: 2026-11-26T23:10:50Z
norm: '1'
sig: 918e4a8de87248e2
body_hash: c3fa20fc9c701e60
raw_hash: 2deac64a33235644
vouched: 2026-11-28T14:03:43Z
lines: 1-1315
supersedes: 01M8C7TQAYKEC7QJ2N6Q8TJFJT
---

Every screen (View.main, View.start) paints its rows with the theme (Fg/Bg Default take its text and background) and View.render lowers the frame to the terminal's depth last (View.view is its frame): spans may use any Color; never lower anywhere else.
