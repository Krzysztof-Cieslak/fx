---
id: 01M8FMJ8P4MN8SMJ3KFKB0337V
anchor: openFast
created: 2026-11-27T23:13:36Z
norm: '1'
---

A failing fast walk (openat2, O_NOFOLLOW_ANY) falls back to the per-component walk, which also names the failing component; only ENOSYS/E2BIG turn the fast walk off for good.
