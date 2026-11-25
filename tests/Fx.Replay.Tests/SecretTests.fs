/// Secrets are never journaled (spec §18): a recorded session whose model calls carry a credential (the
/// wire adds it below `ModelAccess`), whose commands get a variable holding a secret, and a secret
/// resolved through `Secrets`, leaves none of them in the journal or the blob store, and replays exactly.
module Fx.Replay.Tests.SecretTests

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Effects.Live
open Fx.Machines.Host
open Fx.Models
open Fx.Sdk
open Fx.Store
open Fx.Sessions
open Fx.Replay
open Fx.Replay.Tests.Support
open Fx.Replay.Tests.Harness

let private secret = "sk-test-5ecret-VALUE-0123456789"

let private sse (events: (string * string) list) : string =
    events
    |> List.map (fun (name, data) -> $"event: {name}\ndata: {data}\n\n")
    |> String.concat ""

/// An Anthropic Messages stream with one tool call.
let private anthropicCall (n: int) (name: string) (arguments: string) : string =
    sse
        [
            "message_start",
            $"{{\"type\":\"message_start\",\"message\":{{\"id\":\"msg_{n}\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[],\"model\":\"claude-sonnet-5-5\",\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{{\"input_tokens\":400,\"output_tokens\":1}}}}}}"
            "content_block_start",
            $"{{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{{\"type\":\"tool_use\",\"id\":\"toolu_{n}\",\"name\":\"{name}\",\"input\":{{}}}}}}"
            "content_block_delta",
            $"{{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"input_json_delta\",\"partial_json\":{Json.quote arguments}}}}}"
            "content_block_stop", "{\"type\":\"content_block_stop\",\"index\":0}"
            "message_delta",
            "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":30}}"
            "message_stop", "{\"type\":\"message_stop\"}"
        ]

let private cells =
    [
        "// intent: deploy\ncall r = shell.run [\"deploy --token-from-env\"]\nr"
        "// intent: finish\nfinish {\"kind\":\"done\",\"summary\":\"Deployed\"}"
    ]
    |> List.map (fun code -> Json.serialize (Json.Object [ "code", Json.String code ]))

/// Every journal event of the store and every blob, as text.
let private everything (f: Fixture) : string =
    let events =
        f.Store.Streams
        |> List.filter (fun s -> s.Value.StartsWith(JournalLog.StreamKind + "/", StringComparison.Ordinal))
        |> List.collect f.Store.Events
        |> List.map (fun e -> Json.serialize e.Event.Data)

    let blobs =
        f.Blobs.All |> List.map (fun (_, bytes) -> Encoding.UTF8.GetString bytes)

    String.concat "\n" (events @ blobs)

[<Tests>]
let tests =
    testList
        "secrets are never journaled"
        [
            testTask "a credential on the wire, a secret in commands' variables, a resolved secret" {
                let clock = SystemClock.clock
                let calls = ref 0

                let http =
                    InMemoryHttp(
                        clock,
                        fun _ ->
                            calls.Value <- calls.Value + 1

                            match List.tryItem (calls.Value - 1) cells with
                            | Some arguments ->
                                HttpReply.text 200 (anthropicCall calls.Value "cell" arguments)
                                |> HttpReply.withHeader "content-type" "text/event-stream"
                                |> Ok
                                |> Some
                            | None -> None
                    )

                let access =
                    { ModelAccess.create
                          [ Fx.Ext.ModelAnthropic.Anthropic.wire ]
                          [ Fx.Ext.ModelAnthropic.Provider.def ]
                          http.Http
                          clock with
                        Auth =
                            fun _ _ ->
                                Task.FromResult(
                                    Ok
                                        { ResolvedAuth.none with
                                            Headers = [ "x-api-key", secret ]
                                        }
                                )
                    }

                let environments = ref []

                let commands (command: Fx.Effects.Command) (token: CancellationToken) =
                    lock environments (fun () -> environments.Value <- command.Spec.Env :: environments.Value)
                    ran command token

                let f =
                    createFull
                        [ "model.role.main", Json.String "anthropic/claude-sonnet-5-5" ]
                        commands
                        (Exactly [ "DEPLOY_TOKEN", secret ])
                        (Some(ModelAccess.llm access))

                try
                    do! start f "s1"
                    do! prompt f "s1" "p1" "Deploy"
                    let! ending = ended f "s1" 1
                    SessionTests.completed ending

                    // A secret resolved through the capability, journaled by the same recorder kind.
                    let journal =
                        SessionJournal(
                            "secrets",
                            JournalLog.typed f.Store.Log,
                            f.Blobs.Blobs,
                            clock,
                            RecorderSettings.defaults
                        )

                    let secrets =
                        Record.secrets journal "host" (InMemorySecrets.create [ "deploy", secret ])

                    let reference =
                        match SecretRef.tryParse "secret:deploy" with
                        | Ok reference -> reference
                        | Error e -> failtest e

                    match! secrets.Resolve reference CancellationToken.None with
                    | Ok(resolved: Secret) -> Expect.equal (resolved.Reveal()) secret "resolved"
                    | Error e -> failtest $"{e}"

                    do! journal.Close()
                    do! close f

                    // The secret went where it had to: to the provider and into the command's variables.
                    Expect.isTrue
                        (http.Requests
                         |> List.exists (fun r -> r.Headers |> List.exists (fun (_, value) -> value = secret)))
                        "the wire sent the credential"

                    Expect.isTrue
                        (environments.Value
                         |> List.exists (fun env ->
                             match env with
                             | Exactly vars -> vars |> List.contains ("DEPLOY_TOKEN", secret)
                             | _ -> false))
                        "the command got the variable"

                    // And nowhere in the journal or the blob store.
                    let text = everything f
                    Expect.isTrue (text.Contains "Secrets.Resolve") "the resolution is journaled"
                    Expect.isTrue (text.Contains "Llm.Stream") "the model calls are journaled"
                    Expect.isFalse (text.Contains secret) "no secret value"

                    Expect.isFalse
                        (text.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes secret)))
                        "no secret value in base64"

                    Expect.isFalse (text.Contains(Digest.json (Json.String secret))) "no digest of it"

                    let! report = replay f.Recording "s1"
                    expectMatched report
                finally
                    (f :> IDisposable).Dispose()
            }
        ]
