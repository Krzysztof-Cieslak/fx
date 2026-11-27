---
id: 01M89NHB88B3GZY6ZDZ0PCP8FG
anchor: '@file'
created: 2026-11-25T15:35:08Z
norm: '1'
sig: 47aa05747ba118aa
body_hash: bdfd7598cefee71e
raw_hash: e86b01d07418a831
vouched: 2026-11-27T02:59:27Z
lines: 1-476
---

Mount the policy's paths shortest first, and fail the start for a denied or protected path that is an unresolved symbolic link: a mount follows the link (outside the new root), so the path would silently stay visible.
