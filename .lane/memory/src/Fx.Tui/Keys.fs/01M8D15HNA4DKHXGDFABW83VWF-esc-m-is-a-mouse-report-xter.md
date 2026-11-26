---
id: 01M8D15HNA4DKHXGDFABW83VWF
anchor: '@file'
created: 2026-11-26T22:56:07Z
norm: '1'
---

ESC [ M is a mouse report (xterm's first encoding, three characters after it), never a CSI key: terminals without SGR send it for the wheel. Keys.flush must hold a cut-short mouse report as it holds a paste, or its tail is typed into the prompt.
