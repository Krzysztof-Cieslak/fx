---
id: 01M8DB9S5QHNA6AY3PT0GP3JFQ
anchor: '@file'
created: 2026-11-27T01:53:12Z
norm: '1'
sig: 06e768d866788b80
body_hash: 56896e6996ba4958
raw_hash: 58e3ccc4707696d2
vouched: 2026-11-28T13:01:57Z
lines: 1-565
---

A replayed store call does its own part (the append written again; waiting for and reading what the recorded read saw) inside ReplaySegment.AnswerThen, before its entry is done, with CancellationToken.None (only segment.Stop ends the wait): what was recorded after the call (a turn's cancellation, which cancels its cell's host calls) runs as soon as the entry is done, and an append replay loses stalls every later read of the stream.
