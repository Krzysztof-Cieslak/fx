---
id: 01M89NHB8DCMBKM8AAKR6WTY7Y
anchor: '@file'
created: 2026-11-25T15:35:08Z
norm: '1'
---

Syscall numbers are per architecture (unistd_64.h for x86-64, asm-generic/unistd.h for arm64); the filter kills other architectures' and x32 calls, or a 32-bit call would bypass the list.
