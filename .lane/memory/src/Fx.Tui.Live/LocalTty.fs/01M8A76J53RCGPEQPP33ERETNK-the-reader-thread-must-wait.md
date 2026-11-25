---
id: 01M8A76J53RCGPEQPP33ERETNK
anchor: '@file'
created: 2026-11-25T20:43:49Z
norm: '1'
supersedes: 01M8A6FY73K13R0PD1N7KWGX7J
---

The reader thread must wait at most 100 ms per read (select, not poll: macOS's poll does not support terminals) and stop when the terminal is given back: a blocked read would keep eating the shell's keystrokes after the TUI ends.
