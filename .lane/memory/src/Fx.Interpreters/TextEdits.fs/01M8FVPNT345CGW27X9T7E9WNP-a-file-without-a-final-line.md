---
id: 01M8FVPNT345CGW27X9T7E9WNP
anchor: edits
created: 2026-11-28T01:18:20Z
norm: '1'
---

A file without a final line ending keeps lacking one (TextPatch): the last region's new lines split fully there (a final line ending the changes wrote is an empty last line), and only the last region does; fresh ranges are clamped to the new line count.
