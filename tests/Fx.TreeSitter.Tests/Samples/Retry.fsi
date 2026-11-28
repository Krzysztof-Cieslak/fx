/// Retries with backoff (signature).
module Billing.Retry

open System

type RetryConfig = { Attempts: int; Delay: TimeSpan }

val defaults: RetryConfig

module Http =
    val retry: int -> (unit -> 'T) -> 'T
