---
id: 01M8AAN8WBC4GFQSP23XTJ209P
anchor: '@file'
created: 2026-11-25T21:44:17Z
norm: '1'
sig: 2bf2b2535f6f405a
body_hash: e2107df7fc2b6526
raw_hash: 1a9265afeb01569f
lines: 1-680
---

Every Secret Service connection must start with greet (a call whose answer does not depend on the caller) before OpenSession, ReadAlias or anything else: gnome-keyring can dispatch a new connection's first call before it registers the caller, and then aborts on OpenSession or answers "/" to ReadAlias (GNOME/gnome-keyring#190, #206).
