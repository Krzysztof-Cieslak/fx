---
id: 01M8A6FY73K13R0PD1N7KWGX7J
anchor: '@file'
created: 2026-11-25T20:31:27Z
norm: '1'
sig: 41275802be33b25c
body_hash: 35be442de87a6147
raw_hash: edc383e853dd07f9
lines: 1-367
---

The reader thread must poll (100 ms) and stop when the terminal is given back: a blocked read would keep eating the shell's keystrokes after the TUI ends.
