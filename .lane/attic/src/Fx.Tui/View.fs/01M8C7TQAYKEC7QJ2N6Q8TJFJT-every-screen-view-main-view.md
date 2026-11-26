---
id: 01M8C7TQAYKEC7QJ2N6Q8TJFJT
anchor: '@file'
created: 2026-11-26T15:33:18Z
norm: '1'
sig: 918e4a8de87248e2
body_hash: 5252f75374e8d3b4
raw_hash: ad26bb90fce4c8e9
vouched: 2026-11-26T23:31:33Z
lines: 1-1200
supersedes: 01M8C00BBWMM3SF9M9Q9R65AV8
---

Every screen (View.main, View.start) paints its rows with the theme (Fg/Bg Default take its text and background) and View.view lowers the frame to the terminal's depth last: spans may use any Color; never lower anywhere else.
