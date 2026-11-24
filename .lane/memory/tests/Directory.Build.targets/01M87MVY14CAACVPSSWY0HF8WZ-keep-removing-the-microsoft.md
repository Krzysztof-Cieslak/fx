---
id: 01M87MVY14CAACVPSSWY0HF8WZ
anchor: '@file'
created: 2026-11-24T20:44:57Z
norm: '1'
sig: 15a767339566cc27
body_hash: 2aa5327c1a040f39
raw_hash: 728791a2fae375c9
vouched: 2026-11-24T20:49:43Z
lines: 1-11
---

Keep removing the Microsoft.Testing.Extensions.Telemetry builder hook (98058041-...) here, in a .targets file: package props import after Directory.Build.props, and fx allows no telemetry of any kind, test runs included.
