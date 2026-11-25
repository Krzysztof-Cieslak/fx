---
id: 01M88AYWEHAHGETNPCWN8A2YDD
anchor: '@file'
created: 2026-11-25T03:11:03Z
norm: '1'
sig: e240cf2a5f0b9e04
body_hash: a127b189b20f4b06
raw_hash: 9b8b53709b8c25b9
lines: 1-241
---

The kernel and the extension tests install the pinned Bun into the same directory, concurrently under dotnet test --solution: never delete a complete install, and let the first Directory.Move win.
