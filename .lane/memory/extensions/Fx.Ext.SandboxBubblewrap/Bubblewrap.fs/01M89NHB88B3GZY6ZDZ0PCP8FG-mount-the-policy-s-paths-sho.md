---
id: 01M89NHB88B3GZY6ZDZ0PCP8FG
anchor: '@file'
created: 2026-11-25T15:35:08Z
norm: '1'
sig: 47aa05747ba118aa
body_hash: 81620bc38e5cff78
raw_hash: 8b4fae568197565d
lines: 1-476
---

Mount the policy's paths shortest first, and fail the start for a denied or protected path that is an unresolved symbolic link: a mount follows the link (outside the new root), so the path would silently stay visible.
