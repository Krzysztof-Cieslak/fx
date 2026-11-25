---
id: 01M88JQ5S0DQCMQ3Q8V64Y62ZR
anchor: '@file'
created: 2026-11-25T05:26:39Z
norm: '1'
sig: de404010bdb2136a
body_hash: 0fbbbf430a7249d3
raw_hash: 9f018ee2c755961a
lines: 1-96
---

In SDK 11.0.100, dotnet sln add --solution-folder also inserts an empty <Folder Name="/native/" /> before /native/sqlite/; remove it (or edit the .slnx by hand).
