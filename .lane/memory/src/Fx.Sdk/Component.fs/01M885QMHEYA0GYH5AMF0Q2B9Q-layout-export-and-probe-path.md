---
id: 01M885QMHEYA0GYH5AMF0Q2B9Q
anchor: isRelativePath
created: 2026-11-25T01:39:42Z
norm: '1'
---

Layout, export and probe paths are /-separated and relative to the install directory: no empty, . or .. segments, no \ or :. The installer joins them under the install dir and relies on this to never point outside it.
