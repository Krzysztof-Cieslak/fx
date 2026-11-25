---
id: 01M8A7DCAEWR9GNSZ6QW0HCWVJ
anchor: render
created: 2026-11-25T20:47:32Z
norm: '1'
---

Denied directories above a readable root keep their metadata readable (file-read-metadata on literals only): mkdir -p and realpath stat each directory on the way, and bubblewrap shows them as empty mounts.
