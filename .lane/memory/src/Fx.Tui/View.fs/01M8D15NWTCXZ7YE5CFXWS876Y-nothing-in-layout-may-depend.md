---
id: 01M8D15NWTCXZ7YE5CFXWS876Y
anchor: '@file'
created: 2026-11-26T22:56:12Z
norm: '1'
sig: 918e4a8de87248e2
body_hash: 4e28bd73edbe9b0e
raw_hash: 0cfa12d33c25d293
vouched: 2026-11-28T01:44:23Z
lines: 1-1315
---

Nothing in `layout` may depend on Scroll: View.render settles the model on the layout and then draws from it (the footer, which shows the scroll, is drawn from the settled model). The runtime keeps the settled model; a scrolled view stays on its rows only because TranscriptRows records the last frame's.
