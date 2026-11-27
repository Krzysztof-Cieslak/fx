---
id: 01M8DQ94BQTBXYQXYBA3QV7STM
anchor: cancellable
created: 2026-11-27T05:22:33Z
norm: '1'
---

Keep a statement running on the connection while a cancellable operation runs: SQLite clears an interrupt when a statement starts with none running, so without it a cancellation just before a statement starts is lost and the statement runs to its end.
