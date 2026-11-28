/// Extension capabilities attenuated to a manifest (T109b): what is not listed throws, variants read
/// only the extension's own slots, grants ask as the extension and touch only its own requests.
module Fx.Sdk.Tests.CapabilitiesTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Variants
open Fx.Grants
open Fx.Sdk

let private slot (id: string) (value: string) : Resolved =
    {
        Slot = SlotId.create id
        Value = Json.String value
        Source = Source.create Layer.Host "test"
    }

/// What a fake host offers: slots per extension, two packages, grants that record asks.
let private services (asks: ResizeArray<GrantAsk>) : HostServices =
    {
        Slots =
            fun extension ->
                if extension = "acme.a" then
                    [ slot "acme.a.mode" "fast" ]
                else
                    [ slot "acme.b.mode" "slow" ]
        Packages =
            {
                List = fun _ -> Task.FromResult []
                Data = fun name _ -> Task.FromResult(Error(PackagesError.UnknownType name))
            }
        Grants =
            {
                Check = fun _ _ -> Task.FromResult(Ok(GrantCheck.MustAsk [ "ask" ]))
                Request =
                    fun ask _ ->
                        asks.Add ask
                        Task.FromResult(Ok(AskOutcome.Pending(RequestId("r-" + ask.Key))))
                Await = fun _ _ -> Task.FromResult(Ok(Resolution.Cancelled "test"))
                Withdraw = fun _ _ _ -> Task.FromResult(Ok())
            }
    }

let private ask (key: string) : GrantAsk =
    {
        Key = key
        Action = ActionName "file.read"
        Resource = "x"
        Context = GrantContext.none
        Requester =
            {
                Principal = "user:someone:1"
                Agent = Some "agent"
                Extension = Some "acme.other"
            }
        Summary = "Read x"
        Evidence = []
        Suggestions = []
        Scopes = []
        Expires = None
    }

let private wait (work: Task<'T>) : 'T =
    work.WaitAsync(TimeSpan.FromSeconds 10.0).GetAwaiter().GetResult()

[<Tests>]
let tests =
    testList
        "Capabilities"
        [
            test "a capability the manifest does not list throws CapabilityDeniedException" {
                let caps =
                    ExtensionCapabilities.attenuate (services (ResizeArray())) "acme.a" [ Capability.Clock ]

                Expect.throwsT<CapabilityDeniedException>
                    (fun () -> caps.Variants.Read(SlotId.create "acme.a.mode") |> ignore)
                    "variants"

                Expect.throwsT<CapabilityDeniedException> (fun () -> caps.OwnSlots() |> ignore) "own slots"

                Expect.throwsT<CapabilityDeniedException>
                    (fun () -> caps.Packages.List CancellationToken.None |> ignore)
                    "packages"

                Expect.throwsT<CapabilityDeniedException>
                    (fun () -> caps.Grants.Request (ask "k") CancellationToken.None |> ignore)
                    "grants"

                Expect.equal caps.Granted (set [ Capability.Clock ]) "nothing beyond the clock is granted"
            }

            test "variants read only the extension's own slots" {
                let caps =
                    ExtensionCapabilities.attenuate (services (ResizeArray())) "acme.a" [ Capability.Variants ]

                Expect.equal
                    (caps.Variants.Read(SlotId.create "acme.a.mode"))
                    (Some(slot "acme.a.mode" "fast"))
                    "its own"

                Expect.isNone (caps.Variants.Read(SlotId.create "acme.b.mode")) "another extension's"
                Expect.equal (caps.OwnSlots()) [ slot "acme.a.mode" "fast" ] "its slots"
            }

            test "unknown capability names grant nothing" {
                let caps =
                    ExtensionCapabilities.attenuate (services (ResizeArray())) "acme.a" [ "fs.read" ]

                Expect.isEmpty caps.Granted "granted"
            }

            test "grants ask as the extension, and wait for and withdraw only its own requests" {
                let asks = ResizeArray()

                let caps =
                    ExtensionCapabilities.attenuate (services asks) "acme.a" [ Capability.Grants ]

                let outcome = wait (caps.Grants.Request (ask "k1") CancellationToken.None)
                Expect.equal outcome (Ok(AskOutcome.Pending(RequestId "r-ext/acme.a/k1"))) "pending"

                let sent = asks |> Seq.exactlyOne
                Expect.equal sent.Key "ext/acme.a/k1" "the key names the extension"

                Expect.equal
                    sent.Requester
                    {
                        Principal = GrantLog.SystemPrincipal
                        Agent = None
                        Extension = Some "acme.a"
                    }
                    "the extension is the requester"

                Expect.equal
                    (wait (caps.Grants.Await (RequestId "r-ext/acme.a/k1") CancellationToken.None))
                    (Ok(Resolution.Cancelled "test"))
                    "its own request"

                Expect.equal
                    (wait (caps.Grants.Await (RequestId "r-other") CancellationToken.None))
                    (Error(GrantError.UnknownRequest(RequestId "r-other")))
                    "another's request"

                Expect.equal
                    (wait (caps.Grants.Withdraw (RequestId "r-other") "no" CancellationToken.None))
                    (Error(GrantError.UnknownRequest(RequestId "r-other")))
                    "withdrawing another's request"
            }

            test "an extension environment exposes its capabilities" {
                let env =
                    BasicExtensionEnv.attenuated
                        (FakeClock.Create()).Clock
                        (services (ResizeArray()))
                        { Manifest.create "acme.a" "1.0.0" Runtimes.Native with
                            Capabilities = [ Capability.Variants ]
                        }

                let slots = wait (ExtensionEnv.slots env)
                Expect.equal slots [ slot "acme.a.mode" "fast" ] "slots"

                Expect.equal
                    (env.WithScope Scope.root).Capabilities.Granted
                    (set [ Capability.Variants ])
                    "kept across scopes"
            }
        ]
