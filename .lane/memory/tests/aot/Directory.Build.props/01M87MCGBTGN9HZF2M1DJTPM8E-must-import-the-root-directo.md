---
id: 01M87MCGBTGN9HZF2M1DJTPM8E
anchor: '@file'
created: 2026-11-24T20:36:32Z
norm: '1'
---

Must import the root Directory.Build.props directly, not via GetPathOfFileAbove from tests/: tests/Directory.Build.props would make these projects JIT-only Expecto test projects.
