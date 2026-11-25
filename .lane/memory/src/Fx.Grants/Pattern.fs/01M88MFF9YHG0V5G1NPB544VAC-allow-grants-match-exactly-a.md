---
id: 01M88MFF9YHG0V5G1NPB544VAC
anchor: '@file'
created: 2026-11-25T05:57:23Z
norm: '1'
---

Allow grants match exactly and deny grants and policy rules ignoring case (fail closed). Resources are validated canonical before matching; never match a non-canonical resource, or `src/../secrets` would slip under `src/**`.
