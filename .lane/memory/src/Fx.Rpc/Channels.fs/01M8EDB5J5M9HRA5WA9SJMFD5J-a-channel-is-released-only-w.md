---
id: 01M8EDB5J5M9HRA5WA9SJMFD5J
anchor: '@file'
created: 2026-11-27T11:48:09Z
norm: '1'
---

A channel is released only when both directions ended or it was reset; Fx.Rpc cannot know a side will never write, so a side that uses a channel one way must CloseOutput as soon as it has it, or the channel counts against maxChannels at both ends until the connection ends.
