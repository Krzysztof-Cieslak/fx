---
id: 01M87K0MWBSE9DDT99BYV2KJQC
anchor: '@file'
created: 2026-11-24T20:12:35Z
norm: '1'
supersedes: 01M87JT6KXFRQRTC5QHP4EH7T2
---

RID-specific restores (dotnet publish -r) must write to packages.<rid>.lock.json: they add RID sections, and putting those in the committed packages.lock.json breaks --locked-mode restore on other OSes.
