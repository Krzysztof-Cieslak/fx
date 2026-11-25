---
id: 01M8A6FY6H65TYNXZQ0DA1XTN5
anchor: '@file'
created: 2026-11-25T20:31:27Z
norm: '1'
sig: 41275802be33b25c
body_hash: 35be442de87a6147
raw_hash: edc383e853dd07f9
lines: 1-367
---

Never touch System.Console's input or size APIs (ReadKey, In, KeyAvailable, WindowWidth): they initialize .NET's terminal handling, which rewrites termios around child processes and on exit. Read and write the descriptors (the console on Windows) directly.
