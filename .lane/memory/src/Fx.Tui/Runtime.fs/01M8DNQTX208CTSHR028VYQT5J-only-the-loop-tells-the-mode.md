---
id: 01M8DNQTX208CTSHR028VYQT5J
anchor: '@file'
created: 2026-11-27T04:55:38Z
norm: '1'
---

Only the loop tells the model the terminal's size, and it reads the size before it handles a batch with keys or a tick: keys typed after a resize must be handled at the new size (a wheel handled at the old size is clamped away by View.settle). Never post Msg.Resized from another task.
