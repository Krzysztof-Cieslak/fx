/// The conformance suite of identity providers (spec §11.2, docs/modules/identity.md): what every
/// `IdentityProvider` must do, built-in or third-party. A function from a provider and its cases to
/// tests; a provider's own tests link this file and call `IdentityProviderConformance.tests`.
module Fx.Identity.Host.Tests.Conformance.IdentityProviderConformance

open System
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Variants
open Fx.Variants.Fakes
open Fx.Sdk
open Fx.Identity
open Fx.Identity.Host

/// What a provider is checked with.
type Cases =
    {
        /// Credentials the provider must accept, by name, with the id of the principal it vouches for.
        Accepted: (string * Credential * PrincipalId) list
        /// Credentials it must refuse, by name.
        Refused: (string * Credential) list
    }

    override this.ToString() =
        $"{this.Accepted.Length} accepted, {this.Refused.Length} refused"

type private Env =
    {
        Clock: Clock
        Ids: Ids
        Variants: Variants
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface IIds with
        member env.Ids = env.Ids

    interface IVariants with
        member env.Variants = env.Variants

    interface IScopeWith<Env> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

let private run (effect: Eff<IExtensionEnv, 'T>) : 'T =
    let clock = FakeClock.Create()

    let env =
        {
            Extension = "conformance"
            Clock = clock.Clock
            Capabilities = ExtensionCapabilities.none ("conformance")
            Scope = Scope.root
        }
        :> IExtensionEnv

    (effect env).WaitAsync(TimeSpan.FromSeconds 30.0).GetAwaiter().GetResult()

/// Presented credentials of every JSON shape: a provider refuses or accepts them, never throws.
let private junk: Json list =
    [
        Json.Null
        Json.Bool true
        Json.String ""
        Json.String(String('x', 10_000))
        Json.int 7
        Json.Array [ Json.Null; Json.String "a" ]
        Json.Object []
        Json.Object [ "token", Json.Object [ "nested", Json.Array [] ] ]
    ]

/// The suite: `name` names the provider in test names.
let tests (name: string) (provider: IdentityProvider) (cases: Cases) : Test =
    let verify credential = run (provider.Verify credential)

    testList
        ("Identity provider conformance: " + name)
        [
            test "its declaration is valid" {
                Expect.equal (IdentityProvider.validate provider) (Ok()) "IdentityProvider.validate"
            }

            test "it has cases to check" {
                Expect.isNonEmpty cases.Accepted "accepted credentials"
                Expect.isNonEmpty cases.Refused "refused credentials"
            }

            test "it vouches for valid principals of its own, the same each time" {
                for case, credential, expected in cases.Accepted do
                    match verify credential with
                    | Error e -> failtest (case + ": refused: " + string e)
                    | Ok principal ->
                        Expect.equal (Principal.validate principal) (Ok()) (case + ": a valid principal")
                        Expect.equal principal.Provider provider.Id (case + ": its own")
                        Expect.equal principal.Id expected (case + ": the expected principal")
                        Expect.equal (verify credential) (Ok principal) (case + ": the same again")
            }

            test "it refuses what it must, with an error rather than an exception" {
                for case, credential in cases.Refused do
                    match verify credential with
                    | Ok principal -> failtest (case + ": accepted as " + string principal)
                    | Error _ -> ()
            }

            test "it never throws, whatever a client presents" {
                for _, credential in (cases.Accepted |> List.map (fun (n, c, _) -> n, c)) @ cases.Refused do
                    for presented in junk do
                        match
                            verify
                                { credential with
                                    Presented = presented
                                }
                        with
                        | Ok principal ->
                            Expect.equal (Principal.validate principal) (Ok()) "a valid principal"
                            Expect.equal principal.Provider provider.Id "its own"
                        | Error _ -> ()
            }

            test "the principals it vouches for have a role; others' have none" {
                for case, credential, _ in cases.Accepted do
                    let principal =
                        verify credential |> Result.defaultWith (fun e -> failtest (string e))

                    Expect.isNonEmpty (run (provider.Roles principal)) (case + ": a role")

                    let stranger =
                        Principal.create
                            principal.Kind
                            (if provider.Id = "conformance-other" then
                                 "conformance-another"
                             else
                                 "conformance-other")
                            "someone"
                            "someone"
                            Map.empty
                        |> Result.defaultWith failtest

                    Expect.isEmpty (run (provider.Roles stranger)) (case + ": no role for another provider's principal")
            }

            test "the host's authenticator signs its clients in and resumes them" {
                for case, credential, expected in cases.Accepted do
                    let clock = FakeClock.Create()

                    let env =
                        {
                            Clock = clock.Clock
                            Ids = FakeIds(11UL, clock.Clock).Ids
                            Variants = FakeVariants.create IdentitySlots.all []
                            Scope = Scope.root
                        }

                    let contributed =
                        {
                            Extension = "conformance"
                            Type = IdentityProviders.contributionType.Name
                            Id = provider.Id
                            Value = provider
                            Env =
                                {
                                    Extension = "conformance"
                                    Clock = clock.Clock
                                    Capabilities = ExtensionCapabilities.none ("conformance")
                                    Scope = Scope.root
                                }
                                :> IExtensionEnv
                        }

                    let auth = Authenticator.create [ contributed ] credential.Host (SessionTokens())

                    let facts =
                        {
                            Connection = credential.Connection
                            Session = None
                        }

                    let signIn request =
                        (Authenticator.signIn auth facts "conformance" request env)
                            .WaitAsync(TimeSpan.FromSeconds 30.0)
                            .GetAwaiter()
                            .GetResult()

                    match signIn (Some(SignIn.Provider(provider.Id, credential.Presented))) with
                    | Error e -> failtest (case + ": " + string e)
                    | Ok signedIn ->
                        Expect.equal signedIn.Session.Principal.Id expected (case + ": the principal")

                        match signedIn.Token with
                        | None -> failtest (case + ": no token")
                        | Some token ->
                            match signIn (Some(SignIn.Token token)) with
                            | Ok resumed ->
                                Expect.equal resumed.Session signedIn.Session (case + ": resumed")
                                Expect.isNone resumed.Token (case + ": no new token")
                            | Error e -> failtest (case + ": " + string e)
            }
        ]
