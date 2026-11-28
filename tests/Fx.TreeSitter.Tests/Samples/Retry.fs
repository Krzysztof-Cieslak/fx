/// Retries with backoff (a sample for the outline tests).
module Billing.Retry

open System
open System.Threading.Tasks

/// How to retry.
type RetryConfig = { Attempts: int; Delay: TimeSpan }

type Outcome<'T> =
    | Succeeded of 'T
    | GaveUp of attempts: int

exception RetryExhausted of string

let defaults =
    {
        Attempts = 3
        Delay = TimeSpan.FromSeconds 1.0
    }

/// Runs op until it succeeds.
let execute (cfg: RetryConfig) (op: unit -> Task<'T>) : Task<Outcome<'T>> =
    task {
        let mutable result = GaveUp cfg.Attempts
        let helper x = x + 1
        return result
    }

type Clock() =
    member _.Now = DateTimeOffset.UnixEpoch
    member this.Wait(delay: TimeSpan) = Task.Delay delay
    static member Default = Clock()

module Http =
    let retry n f =
        execute { defaults with Attempts = n } f

    type Policy =
        abstract Decide: int -> bool
