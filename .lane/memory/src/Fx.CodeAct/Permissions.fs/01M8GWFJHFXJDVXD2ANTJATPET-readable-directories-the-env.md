---
id: 01M8GWFJHFXJDVXD2ANTJATPET
anchor: isReadable
created: 2026-11-28T10:51:11Z
norm: '1'
---

Readable directories (the env blob store) waive grants for reads only: fs.write and redirections there stay writes outside the workspace, and the check compares canonical paths by the OS case rules with a separator, so a sibling with the same prefix stays outside.
