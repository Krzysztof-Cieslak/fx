---
id: 01M8E31QK02DF12QSY5NRVBRBS
anchor: let put
created: 2026-11-27T08:48:14Z
norm: '1'
---

VtScreen must keep the cursor on the last column after drawing there (as a terminal with wrap off does), not one past it: the full-width tests rely on it to catch an EL that erases the last column.
