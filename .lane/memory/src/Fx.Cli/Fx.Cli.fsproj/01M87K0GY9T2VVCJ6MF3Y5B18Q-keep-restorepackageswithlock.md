---
id: 01M87K0GY9T2VVCJ6MF3Y5B18Q
anchor: '@file'
created: 2026-11-24T20:12:31Z
norm: '1'
---

Keep RestorePackagesWithLockFile=false while PublishAot is on: even a RID-less restore adds the host RID, so a lockfile here differs per OS and breaks --locked-mode restore on the others.
