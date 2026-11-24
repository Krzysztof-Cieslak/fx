---
id: 01M87TVGA6DMNAS2Y8JX15EBQF
anchor: spawn
created: 2026-11-24T22:29:35Z
norm: '1'
---

Keep the parent's slave descriptor open until posix_spawn returns, and close it right after: macOS rejects ioctls on a master whose slave was never opened and zeroes the size on a first open; holding it longer would keep the output from ending.
