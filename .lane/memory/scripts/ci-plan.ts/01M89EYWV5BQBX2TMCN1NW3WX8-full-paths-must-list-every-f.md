---
id: 01M89EYWV5BQBX2TMCN1NW3WX8
anchor: '@file'
created: 2026-11-25T13:40:12Z
norm: '1'
sig: 977c0f8898e6cb06
body_hash: 99f878579a6d550c
raw_hash: 04baed610fb915bb
vouched: 2026-11-25T14:06:03Z
lines: 1-174
---

FULL_PATHS must list every file whose behaviour can differ per architecture or runner (P/Invoke, native libraries, per-target pins, build settings, workflows): the fast tier runs only linux-x64, linux-musl-x64, osx-arm64 and win-x64, so anything else is first checked on main.
