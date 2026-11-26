---
id: 01M8A8K6ANZQ316QB3G5BP10FN
anchor: '@file'
created: 2026-11-25T21:08:11Z
norm: '1'
sig: 8e7430f31889b761
body_hash: 2055b65c74738671
raw_hash: 83675b92a58ae150
vouched: 2026-11-26T04:51:27Z
lines: 1-519
---

SANDBOX_TESTS=1 makes a missing or failing bubblewrap or Seatbelt a test failure instead of a skip: every Linux step that sets it (the os job's tests, the sandbox steps) needs bubblewrap installed and kernel.apparmor_restrict_unprivileged_userns=0 first.
