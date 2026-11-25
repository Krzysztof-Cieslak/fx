---
id: 01M88MFF9YHG0V5G1NPB544VAC
anchor: '@file'
created: 2026-11-25T05:57:23Z
norm: '1'
sig: 16d4050a8067cd99
body_hash: 9a38f42d33cd8684
raw_hash: 9224c8e80e82e8bd
lines: 1-469
---

Allow grants match exactly and deny grants and policy rules ignoring case (fail closed). Resources are validated canonical before matching; never match a non-canonical resource, or `src/../secrets` would slip under `src/**`.
