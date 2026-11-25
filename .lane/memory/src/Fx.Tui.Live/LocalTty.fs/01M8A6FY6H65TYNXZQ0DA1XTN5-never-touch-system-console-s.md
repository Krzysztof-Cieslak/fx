---
id: 01M8A6FY6H65TYNXZQ0DA1XTN5
anchor: '@file'
created: 2026-11-25T20:31:27Z
norm: '1'
sig: 41275802be33b25c
body_hash: f876ddc1fbe70a7a
raw_hash: e5c9eb3dfc81636a
vouched: 2026-11-25T20:43:47Z
lines: 1-367
---

Never touch System.Console's input or size APIs (ReadKey, In, KeyAvailable, WindowWidth): they initialize .NET's terminal handling, which rewrites termios around child processes and on exit. Read and write the descriptors (the console on Windows) directly.
