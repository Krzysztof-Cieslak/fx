namespace Fx.Effects

open System.Runtime.ExceptionServices
open System.Threading
open System.Threading.Tasks

/// An effect that needs the capabilities in the environment `'R` and produces a `'T` (spec §4.2).
///
/// - `'R` is constrained with "has" interfaces (`#IClock`, `#IShell`, …): the signature states what
///   the effect may use, and the environment value it is handed is its authority.
/// - Defects are exceptions (a faulted task). Expected failures are a `Result` inside `'T`; use
///   `effResult { }` for them.
/// - Invoking an effect starts it: it runs synchronously up to its first incomplete await.
type Eff<'R, 'T> = 'R -> Task<'T>

/// Identifies a trace span. Span ids are local to one trace store and never leave the machine.
[<Struct>]
type SpanId =
    | SpanId of uint64

    override this.ToString() =
        let (SpanId n) = this
        n.ToString("x16")

/// The ambient context an effect runs in: what structured combinators narrow for their children.
type Scope =
    {
        /// Cancellation of everything running in this scope. Capabilities pass it to their calls.
        Cancellation: CancellationToken
        /// The innermost open trace span: the parent of spans and events started in this scope.
        Span: SpanId option
    }

    override this.ToString() =
        let span =
            match this.Span with
            | Some id -> string id
            | None -> "none"

        $"Scope(cancelled = {this.Cancellation.IsCancellationRequested}, span = {span})"

module Scope =
    /// A scope that is never cancelled and has no open span.
    let root: Scope =
        {
            Cancellation = CancellationToken.None
            Span = None
        }

    /// A root scope cancelled by the given token.
    let ofCancellation (token: CancellationToken) : Scope = { root with Cancellation = token }

/// An environment that carries a `Scope`. Every capability call passes the scope's cancellation.
type IScope =
    abstract Scope: Scope

/// An environment that can be rebuilt with another scope, so combinators can run children in a
/// narrower one (`Eff.race` cancels losers, `Trace.span` opens a span). Implement it on the concrete
/// environment type `'R` itself: `WithScope` returns a copy of the environment with the new scope.
type IScopeWith<'R> =
    inherit IScope
    abstract WithScope: Scope -> 'R

/// Helpers shared by the modules of this assembly.
module internal Internal =
    /// A completed `Task<unit>`, shared to avoid allocating one per call.
    let completedUnit: Task<unit> = Task.FromResult()

    /// Rethrows an exception with its original stack trace.
    let rethrow (e: exn) : 'T =
        ExceptionDispatchInfo.Capture(e).Throw()
        Unchecked.defaultof<'T>
