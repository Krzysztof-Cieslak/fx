---
id: 01M8A8K6ANZQ316QB3G5BP10FN
anchor: '@file'
created: 2026-11-25T21:08:11Z
norm: '1'
---

SANDBOX_TESTS=1 makes a missing or failing bubblewrap or Seatbelt a test failure instead of a skip: every Linux step that sets it (the os job's tests, the sandbox steps) needs bubblewrap installed and kernel.apparmor_restrict_unprivileged_userns=0 first.
