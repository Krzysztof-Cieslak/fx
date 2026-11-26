---
id: 01M8D1EM8K0N1G6QQW1J06HFKT
anchor: '@file'
created: 2026-11-26T23:01:05Z
norm: '1'
sig: db88ec098994fb1c
body_hash: 31a5fc38fbc7cc59
raw_hash: 76e31c0d5764b904
lines: 1-436
---

NuGet's user config stays denied: sandboxed processes get DOTNET_CLI_HOME in the environment's cache, where NuGet writes a fresh one; never copy or sanitize the user's config into the sandbox (credentials hide in forms a sanitizer misses).
