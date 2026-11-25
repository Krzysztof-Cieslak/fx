namespace Fx.Host

open System
open System.Collections.Generic
open Fx.Effects
open Fx.Models
open Fx.Sessions.Host

/// What the model is writing in a session's turn, as it writes it (spec §19.2: the transcript
/// streams): a piece of the reply's text, or the name of a tool call it started. Not durable and
/// not part of the log: the reply is recorded in the context log when it is complete, and a client
/// that connects later sees it there.
type StreamingNotice =
    {
        Session: string
        Turn: int
        /// The first piece of a model call: what was streamed before belongs to an earlier call.
        Fresh: bool
        /// Text the reply adds.
        Text: string
        /// A tool call the reply started (`cell`, `read`, …).
        Tool: string option
    }

    override this.ToString() =
        $"{this.Session} turn {this.Turn}: {this.Text.Length} chars"

/// The model streams of the host's running turns, for clients that follow a session
/// (`session/context`). Subscribers are called on the stream's thread and must not block.
[<Sealed>]
type Streams() =
    let gate = obj ()

    let mutable subscribers: Map<string, (int64 * (StreamingNotice -> unit)) list> =
        Map.empty

    let mutable next = 0L

    member _.Publish(notice: StreamingNotice) =
        let current =
            lock gate (fun () -> Map.tryFind notice.Session subscribers |> Option.defaultValue [])

        for _, handler in current do
            try
                handler notice
            with _ ->
                // A subscriber's failure is its own; the turn goes on.
                ()

    /// Calls `handler` with what the session's turns stream, until disposed.
    member _.Subscribe(session: string, handler: StreamingNotice -> unit) : IDisposable =
        let id =
            lock gate (fun () ->
                next <- next + 1L

                subscribers <-
                    Map.add
                        session
                        ((next, handler) :: (Map.tryFind session subscribers |> Option.defaultValue []))
                        subscribers

                next)

        { new IDisposable with
            member _.Dispose() =
                lock gate (fun () ->
                    match Map.tryFind session subscribers with
                    | Some list ->
                        match list |> List.filter (fun (i, _) -> i <> id) with
                        | [] -> subscribers <- Map.remove session subscribers
                        | rest -> subscribers <- Map.add session rest subscribers
                    | None -> ())
        }

    override _.ToString() = "Streams"

[<RequireQualifiedAccess>]
module Streams =
    /// Model access for one turn whose responses are published as they arrive: their text and the
    /// tool calls they start. The requests and responses are not changed.
    let tap (streams: Streams) (session: string) (turn: int) (llm: Llm) : Llm =
        {
            Stream =
                fun request cancellation ->
                    task {
                        let! stream = llm.Stream request cancellation
                        let parts = Dictionary<int, PartStart>()
                        let fresh = ref true

                        let publish (text: string) (tool: string option) =
                            streams.Publish
                                {
                                    Session = session
                                    Turn = turn
                                    Fresh = fresh.Value
                                    Text = text
                                    Tool = tool
                                }

                            fresh.Value <- false

                        let observe (event: ChatEvent) =
                            match event with
                            | ChatEvent.PartStarted(index, start) ->
                                parts[index] <- start

                                match start with
                                | PartStart.ToolCall(_, name) -> publish "" (Some name)
                                | _ -> ()
                            | ChatEvent.Delta(index, text) ->
                                match parts.TryGetValue index with
                                | true, PartStart.Text -> publish text None
                                | _ -> ()
                            | _ -> ()

                        return ChatStream.tap observe stream
                    }
        }

    /// A turn runner whose turns' model calls are published (`tap`), as they run and when they are
    /// recovered.
    let runner (streams: Streams) (inner: TurnRunner<HostEnv>) : TurnRunner<HostEnv> =
        let tapped (context: TurnContext<HostEnv>) (env: HostEnv) =
            { env with
                Llm = tap streams context.Turn.Session context.Turn.Turn env.Llm
            }

        {
            Run = fun context -> Eff.local (tapped context) (inner.Run context)
            Recover = fun context -> Eff.local (tapped context) (inner.Recover context)
        }
