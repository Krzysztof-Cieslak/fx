---
id: 01M88BD3B0YQ17MAHEEKX02Z3A
anchor: '@file'
created: 2026-11-25T03:18:48Z
norm: '1'
sig: 977c0f8898e6cb06
body_hash: 186df14c46598a02
raw_hash: 43a3cd043630c5e3
vouched: 2026-11-25T04:04:53Z
lines: 1-173
---

The consumer must inherit nothing from the repository's build (its own empty Directory.Build.props/targets/Packages.props, --configfile, its own --packages folder): otherwise it would not prove the packages are complete, and 0.0.0 packages would go stale in the global NuGet cache.
