---
id: 01M89NHQQ3KDCTQBBH945887F7
anchor: sandbox
created: 2026-11-25T15:35:21Z
norm: '1'
---

SANDBOX_TESTS=1 makes a missing or failing bubblewrap or Seatbelt a test failure instead of a skip; the Linux jobs must install bubblewrap and set kernel.apparmor_restrict_unprivileged_userns=0 first.
