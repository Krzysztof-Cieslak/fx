---
id: 01M87K0GYAEMAA6XACF9PHG967
anchor: '@file'
created: 2026-11-24T20:12:31Z
norm: '1'
sig: 86e0a4d4f900f574
body_hash: 87762d5bfd14d02b
raw_hash: 52c7f96825b0a216
lines: 1-12
---

Restore must use only nuget.org (with DisableImplicitLibraryPacksFolder in Directory.Build.props): the SDK's bundled FSharp.Core nupkg has a different content hash than nuget.org's, which breaks locked restore across machines.
