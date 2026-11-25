---
id: 01M883QK3BZJZ4Y535ZDHW21B2
anchor: '@file'
created: 2026-11-25T01:04:44Z
norm: '1'
sig: 5343940f872f5e11
body_hash: 3123c0480155cf30
raw_hash: 5c7af9bec5dbd53e
lines: 1-1853
supersedes: 01M87Q5DH3JDKR7B9K0BF01WPG
---

Union encoders and the record's tuple of fields must only ever reach single-parameter functions (return's lambda), never curried ones: a curried function, partial application or curried call instantiates FSharp.Core's closures over all of them, and ILC aborts that from 10 function types or one generic nested 5 deep (IL3054). The old match function curried over one encoder per case failed at 7 cases.
