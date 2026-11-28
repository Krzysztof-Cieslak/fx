---
id: 01M8H7G3S58S72C3P5YV5CFK8X
anchor: '@file'
created: 2026-11-28T14:03:43Z
norm: '1'
supersedes: 01M8H3Z0H93C1XK8D7M9TEJG0X
---

Arguments decode only with the binding's codecs through Value.decode, so references never decode into data (spec §6.6, §18). Two exceptions, both explicit: Arg.valueOrHandle resolves a handle only through the kind's own session table and declared type, and Arg.value takes the value as it came (references stay references). A binding that throws is a defect, expected failures are HostCallError.
