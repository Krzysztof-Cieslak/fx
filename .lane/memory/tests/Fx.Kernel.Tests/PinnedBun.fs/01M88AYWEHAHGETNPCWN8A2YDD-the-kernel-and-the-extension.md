---
id: 01M88AYWEHAHGETNPCWN8A2YDD
anchor: '@file'
created: 2026-11-25T03:11:03Z
norm: '1'
---

The kernel and the extension tests install the pinned Bun into the same directory, concurrently under dotnet test --solution: never delete a complete install, and let the first Directory.Move win.
