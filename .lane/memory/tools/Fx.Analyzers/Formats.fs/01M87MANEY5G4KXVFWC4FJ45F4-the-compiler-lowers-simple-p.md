---
id: 01M87MANEY5G4KXVFWC4FJ45F4
anchor: '@file'
created: 2026-11-24T20:35:31Z
norm: '1'
---

The compiler lowers simple printf specifiers in string-typed interpolated strings ($"%d{x}") to String.Concat before the typed tree, so they are only visible in the syntax tree; the typed pass skips PrintfFormat literals at interpolated-string ranges to avoid double reports.
