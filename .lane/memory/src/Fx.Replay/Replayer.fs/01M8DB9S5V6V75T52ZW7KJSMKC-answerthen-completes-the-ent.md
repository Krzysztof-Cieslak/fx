---
id: 01M8DB9S5V6V75T52ZW7KJSMKC
anchor: '@file'
created: 2026-11-27T01:53:12Z
norm: '1'
sig: 06e768d866788b80
body_hash: b98210fb02ae520f
raw_hash: 50f57a1e2d1768be
lines: 1-427
---

AnswerThen completes the entry only after `apply` has finished: a replay interpreter with work of its own after the recorded answer (writing, waiting, reading) does it in `apply`, never after Answer returns, and never gives up on the caller's cancellation there.
