---
id: 01M87M4ZH3K8NDH43R5SYTMSZ4
anchor: '@file'
created: 2026-11-24T20:32:25Z
norm: '1'
sig: e3b0c44298fc1c14
body_hash: 57a26c46fa2a94e4
raw_hash: 08b3b45e3ddf2356
lines: 1-222
---

The child environment must be built from the allowlist, never merged into the parent's: BUN_OPTIONS (--preload), NODE_PATH, BUN_INSPECT (opens a debugger port) and BUN_JSC_* are all effective in Bun 1.4.5. Deny patterns match case-insensitively because Windows environment names are case-insensitive.
