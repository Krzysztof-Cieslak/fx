---
id: 01M8A8KBM8MX9HR646ET1PEF97
anchor: '@file'
created: 2026-11-25T21:08:17Z
norm: '1'
sig: 977c0f8898e6cb06
body_hash: 2f45bddc177934e3
raw_hash: 0c24444e8a8124a8
vouched: 2026-11-25T21:58:56Z
lines: 1-240
supersedes: 01M89EYWV5BQBX2TMCN1NW3WX8
---

PLATFORM_PATHS must list every file whose behaviour can differ per architecture or runner (P/Invoke, native libraries, per-target pins, build settings, workflows): pull requests run only linux-x64, osx-arm64 and win-x64, and the plan's warning is what asks for the ci:full label before such a change merges.
