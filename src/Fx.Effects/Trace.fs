namespace Fx.Effects

open System

[<RequireQualifiedAccess>]
type Level =
    | Debug
    | Info
    | Warning
    | Error

    override this.ToString() =
        match this with
        | Debug -> "debug"
        | Info -> "info"
        | Warning -> "warning"
        | Error -> "error"

[<RequireQualifiedAccess>]
type AttrValue =
    | Text of string
    | Int of int64
    | Float of float
    | Bool of bool

    override this.ToString() =
        match this with
        | Text text -> text
        | Int n -> string n
        | Float x -> x.ToString("R", Globalization.CultureInfo.InvariantCulture)
        | Bool b -> if b then "true" else "false"

/// A key/value attribute on a span or event.
type Attr =
    {
        Key: string
        Value: AttrValue
    }

    override this.ToString() = $"{this.Key}={this.Value}"

module Attr =
    let text key value =
        {
            Key = key
            Value = AttrValue.Text value
        }

    let int key (value: int64) =
        {
            Key = key
            Value = AttrValue.Int value
        }

    let float key value =
        {
            Key = key
            Value = AttrValue.Float value
        }

    let bool key value =
        {
            Key = key
            Value = AttrValue.Bool value
        }

/// How a span ended.
[<RequireQualifiedAccess>]
type SpanStatus =
    | Ok
    | Cancelled
    /// The span's effect threw. The message is for local display only.
    | Failed of exceptionType: string * message: string

    override this.ToString() =
        match this with
        | Ok -> "ok"
        | Cancelled -> "cancelled"
        | Failed(name, _) -> $"failed ({name})"

/// The local tracing capability: spans and events (spec §15 observability). Traces stay on this
/// machine: there is no exporter and there never will be one that sends data elsewhere (no
/// telemetry of any kind, spec §18). Spec §4.2 calls this `ITelemetry`.
///
/// Interpreters: `TraceCollector` (in memory), `Trace.none`.
type Trace =
    {
        /// Starts a span under the parent; returns its id.
        StartSpan: SpanId option -> string -> Attr list -> SpanId
        EndSpan: SpanId -> SpanStatus -> unit
        /// Records an event in a span (or outside any).
        Event: SpanId option -> Level -> string -> Attr list -> unit
    }

    override _.ToString() = "Trace"

/// An environment with local tracing.
type ITrace =
    abstract Trace: Trace

/// A record in a trace.
type TraceRecord =
    | SpanStarted of id: SpanId * parent: SpanId option * name: string * attrs: Attr list * at: Instant
    | SpanEnded of id: SpanId * status: SpanStatus * at: Instant
    | EventRecorded of span: SpanId option * level: Level * message: string * attrs: Attr list * at: Instant

    override this.ToString() =
        let attrs (list: Attr list) =
            list |> List.map string |> String.concat " "

        let span =
            function
            | Some(id: SpanId) -> string id
            | None -> "-"

        match this with
        | SpanStarted(id, parent, name, list, at) -> $"{at} start {id} parent={span parent} {name} {attrs list}"
        | SpanEnded(id, status, at) -> $"{at} end {id} {status}"
        | EventRecorded(id, level, message, list, at) -> $"{at} {level} span={span id} {message} {attrs list}"

/// Keeps a trace in memory, timestamped by a clock. Span ids count up from 1, so a trace taken with
/// a fake clock is deterministic. Thread-safe.
type TraceCollector(clock: Clock) =
    let gate = obj ()
    let records = ResizeArray<TraceRecord>()
    let mutable lastId = 0UL

    let add record =
        lock gate (fun () -> records.Add record)

    member val Trace: Trace =
        {
            StartSpan =
                fun parent name attrs ->
                    let id =
                        lock gate (fun () ->
                            lastId <- lastId + 1UL
                            SpanId lastId)

                    add (SpanStarted(id, parent, name, attrs, clock.Now()))
                    id
            EndSpan = fun id status -> add (SpanEnded(id, status, clock.Now()))
            Event = fun span level message attrs -> add (EventRecorded(span, level, message, attrs, clock.Now()))
        }

    /// Everything recorded so far, in order.
    member _.Records: TraceRecord list = lock gate (fun () -> List.ofSeq records)

module Trace =
    /// Discards everything.
    let none: Trace =
        {
            StartSpan = fun _ _ _ -> SpanId 0UL
            EndSpan = fun _ _ -> ()
            Event = fun _ _ _ _ -> ()
        }

    /// Records an event in the current span.
    let event<'R when 'R :> ITrace and 'R :> IScope>
        (level: Level)
        (message: string)
        (attrs: Attr list)
        : Eff<'R, unit> =
        fun env ->
            env.Trace.Event env.Scope.Span level message attrs
            Internal.completedUnit

    let debug message attrs = event Level.Debug message attrs
    let info message attrs = event Level.Info message attrs
    let warning message attrs = event Level.Warning message attrs
    let error message attrs = event Level.Error message attrs

    /// Runs the effect in a new span, a child of the current one, and ends the span with the outcome.
    let span<'R, 'T when 'R :> ITrace and 'R :> IScopeWith<'R>>
        (name: string)
        (attrs: Attr list)
        (effect: Eff<'R, 'T>)
        : Eff<'R, 'T> =
        fun env ->
            let trace = env.Trace
            let id = trace.StartSpan env.Scope.Span name attrs
            let child = env.WithScope { env.Scope with Span = Some id }

            task {
                try
                    let! value = Eff.run child effect
                    trace.EndSpan id SpanStatus.Ok
                    return value
                with e ->
                    let status =
                        match e with
                        | :? OperationCanceledException -> SpanStatus.Cancelled
                        | e -> SpanStatus.Failed(e.GetType().Name, e.Message)

                    trace.EndSpan id status
                    return Internal.rethrow e
            }
