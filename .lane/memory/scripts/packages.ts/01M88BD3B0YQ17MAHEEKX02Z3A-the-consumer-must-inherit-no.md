---
id: 01M88BD3B0YQ17MAHEEKX02Z3A
anchor: '@file'
created: 2026-11-25T03:18:48Z
norm: '1'
---

The consumer must inherit nothing from the repository's build (its own empty Directory.Build.props/targets/Packages.props, --configfile, its own --packages folder): otherwise it would not prove the packages are complete, and 0.0.0 packages would go stale in the global NuGet cache.
