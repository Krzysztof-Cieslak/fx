---
id: 01M8A8KBEX7VXQ279FQ2FD9502
anchor: '@file'
created: 2026-11-25T21:08:16Z
norm: '1'
supersedes: 01M8860RVMP7Y92WZ02KFSTA0K
---

Commands in the Alpine container run through `docker exec alpine` ($IN, also in .github/scripts/aot-smoke.sh), which drops standard input: anything piped into the binary needs `docker exec -i`.
