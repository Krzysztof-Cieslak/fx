"""Retries with backoff (a sample for the outline tests)."""

import asyncio
from dataclasses import dataclass

MAX_ATTEMPTS = 10


@dataclass
class RetryConfig:
    attempts: int = 3
    delay: float = 1.0


class Retrier:
    def __init__(self, clock):
        self.clock = clock

    @property
    def count(self):
        return self._count

    async def execute(self, cfg, op):
        def helper(x):
            return x + 1

        for _ in range(cfg.attempts):
            await op()


def retry(n, f):
    return Retrier(None).execute(RetryConfig(n), f)
