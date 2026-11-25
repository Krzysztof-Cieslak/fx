---
id: 01M88AYWEHAHGETNPCWN8A2YDD
anchor: '@file'
created: 2026-11-25T03:11:03Z
norm: '1'
sig: 5d1438b506ec7a84
body_hash: c859197f20ecca43
raw_hash: b62e14bf0dd10d4f
vouched: 2026-11-25T05:00:54Z
lines: 1-241
---

The kernel and the extension tests install the pinned Bun into the same directory, concurrently under dotnet test --solution: never delete a complete install, and let the first Directory.Move win.
