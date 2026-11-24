---
id: 01M87M5CCX59D4TF653CJ2Y83D
anchor: '@file'
created: 2026-11-24T20:32:38Z
norm: '1'
---

bun check must always get --no-script: in 1.4.5 it otherwise runs a "check" script from any package.json in or above the check directory, even with -p. --no-follow-imports (online docs) does not exist in 1.4.5.
