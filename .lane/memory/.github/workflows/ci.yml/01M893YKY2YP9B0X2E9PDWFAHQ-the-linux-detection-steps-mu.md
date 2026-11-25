---
id: 01M893YKY2YP9B0X2E9PDWFAHQ
anchor: keyring
created: 2026-11-25T10:27:48Z
norm: '1'
---

The Linux detection steps must run before the first `gnome-keyring-daemon --unlock`: once a login keyring exists, `--start` no longer gives the no-default-collection state.
