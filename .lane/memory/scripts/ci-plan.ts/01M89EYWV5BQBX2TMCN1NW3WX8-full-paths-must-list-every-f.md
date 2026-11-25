---
id: 01M89EYWV5BQBX2TMCN1NW3WX8
anchor: '@file'
created: 2026-11-25T13:40:12Z
norm: '1'
sig: 977c0f8898e6cb06
body_hash: 40eb62d840e25a56
raw_hash: b6dcf169b715b882
vouched: 2026-11-25T20:23:16Z
lines: 1-174
---

FULL_PATHS must list every file whose behaviour can differ per architecture or runner (P/Invoke, native libraries, per-target pins, build settings, workflows): the fast tier runs only linux-x64, linux-musl-x64, osx-arm64 and win-x64, so anything else is first checked on main.
