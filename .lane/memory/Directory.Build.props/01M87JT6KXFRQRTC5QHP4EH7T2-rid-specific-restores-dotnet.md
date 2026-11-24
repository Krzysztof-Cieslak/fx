---
id: 01M87JT6KXFRQRTC5QHP4EH7T2
anchor: '@file'
created: 2026-11-24T20:09:03Z
norm: '1'
sig: 15a767339566cc27
body_hash: 0d9638b848739269
raw_hash: 0568f1ec7df0cf79
lines: 1-59
---

RID-specific restores (dotnet publish -r) must write to packages.<rid>.lock.json: they add host-specific ILCompiler packages, and putting those in packages.lock.json breaks --locked-mode restore on other OSes.
