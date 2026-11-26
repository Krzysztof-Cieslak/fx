---
id: 01M8AMHSCZRHMDE5KXM6QR5MTC
anchor: '@file'
created: 2026-11-26T00:37:08Z
norm: '1'
supersedes: 01M88NNWM7H8TMH7RSAKF2CGN6
---

The approval request key is component.install/<install name>/<holder>/<attempt> (Approvals.key): derived from what asks, so the pipeline retrying one attempt asks once while a later attempt or holder (a later run's environment) asks again. Never key it by the install alone (an answer for once, or a request nobody could answer, would then stand for every later run), nor by time or random ids.
