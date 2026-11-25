namespace Fx.Models

open System
open System.Threading
open System.Threading.Tasks
open Fx.Effects

/// The model capability (spec §4.2 `ILlm`): every model-bound request of a session, agent,
/// summarizer or judgment goes through it. The host's interpreter (`ModelAccess` in Fx.Sdk) fits
/// each request to its model, passes it through the model-egress hooks, and only then gives it to the
/// model's wire (spec §18).
///
/// Interpreters: `ModelAccess.llm` (Fx.Sdk), `ScriptedLlm` (Fx.Models.Fakes).
type Llm =
    {
        /// Starts a response. Failures, before or during the response, are a `Failed` event of the
        /// stream; the caller's cancellation is an `OperationCanceledException`.
        Stream: ChatRequest -> CancellationToken -> Task<ChatStream>
    }

    override _.ToString() = "Llm"

/// An environment that can call models.
type ILlm =
    abstract Llm: Llm

module Llm =
    /// Starts a response. Cancelled with the scope; close the stream when done.
    let stream<'R when 'R :> ILlm and 'R :> IScope> (request: ChatRequest) : Eff<'R, ChatStream> =
        fun env -> env.Llm.Stream request env.Scope.Cancellation

    /// Runs a request to its end: the response, or the failure with what arrived before it.
    let complete<'R when 'R :> ILlm and 'R :> IScope>
        (request: ChatRequest)
        : Eff<'R, Result<ChatResponse, ChatFailure>> =
        eff {
            let! stream = stream request
            let! state = ChatStream.fold stream
            return ChatAggregate.result state
        }

namespace Fx.Models.Fakes

open System.Threading.Tasks
open Fx.Models

/// A model that answers from a script: `respond` gets each request and returns the events of its
/// response, or `None` for a request the script does not expect (a defect in the test: it throws).
/// `Requests` records every request in order.
[<Sealed>]
type ScriptedLlm(respond: ChatRequest -> ChatEvent list option) =
    let gate = obj ()
    let requests = ResizeArray<ChatRequest>()

    let stream (request: ChatRequest) (cancellation: System.Threading.CancellationToken) =
        if cancellation.IsCancellationRequested then
            Task.FromCanceled<ChatStream> cancellation
        else
            lock gate (fun () -> requests.Add request)

            match respond request with
            | Some events -> Task.FromResult(ChatStream.ofEvents events)
            | None -> invalidOp $"ScriptedLlm: no response scripted for {request}"

    /// A model that answers every request with the same response.
    static member Always(response: ChatResponse) =
        ScriptedLlm(fun _ -> Some(ChatEvents.ofResponse response))

    member val Llm: Llm = { Stream = stream }

    member _.Requests: ChatRequest list = lock gate (fun () -> List.ofSeq requests)
