---
id: 01M885QT7144N2HXM32E92KCGV
anchor: activate
created: 2026-11-25T01:39:48Z
norm: '1'
---

An install directory must only ever appear complete and probed: stage and probe in .staging, then one rename under the root lock is the only commit point. Never write into <root>/<id>/<dir> after activation; other installers treat a matching receipt as a finished install.
