/// The environment the identity host runs in during tests, a provider to test it with, and helpers.
module Fx.Identity.Host.Tests.Support

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

type Env =
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

    override env.ToString() = "Env(" + string env.Scope + ")"

type Fakes =
    {
        Env: Env
        Clock: FakeClock
    }

    override _.ToString() = "Fakes"

/// An environment on a fake clock, seeded ids and the identity's slots with these selections.
let fakesWith (selections: (string * Json) list) : Fakes =
    let clock = FakeClock.Create()

    {
        Env =
            {
                Clock = clock.Clock
                Ids = FakeIds(7UL, clock.Clock).Ids
                Variants = FakeVariants.create IdentitySlots.all selections
                Scope = Scope.root
            }
        Clock = clock
    }

let fakes () : Fakes = fakesWith []

/// Waits for a task, failing the test after 30 s; a failed task rethrows its exception.
let wait (task: Task<'T>) : 'T =
    try
        task.WaitAsync(TimeSpan.FromSeconds 30.0).GetAwaiter().GetResult()
    with :? TimeoutException ->
        failtest "the task did not complete in 30 s"

let runIn (env: 'R) (effect: Eff<'R, 'T>) : 'T = wait (Eff.run env effect)

let ok (result: Result<'T, 'E>) : 'T =
    match result with
    | Ok value -> value
    | Error e -> failtest (string e)

let host: HostIdentity =
    {
        Machine = "M1"
        Account = { Id = "1000"; Name = Some "alice" }
    }

/// The extension environment a provider runs in.
let extensionEnv (clock: Clock) (id: string) : IExtensionEnv =
    {
        Extension = id
        Clock = clock
        Capabilities = ExtensionCapabilities.none id
        Scope = Scope.root
    }
    :> IExtensionEnv

/// A provider contributed by an extension, with its environment.
let contributed (clock: Clock) (provider: IdentityProvider) : Contributed<IdentityProvider> =
    {
        Extension = "test." + provider.Id
        Type = IdentityProviders.contributionType.Name
        Id = provider.Id
        Value = provider
        Env = extensionEnv clock ("test." + provider.Id)
    }

/// A test provider (`names`): it vouches for whoever the client names, `{"name": "alice"}`, and
/// knows alice (owner), bob (viewer) and carol (no role). `rogue` vouches with someone else's
/// principals.
let names: IdentityProvider =
    {
        Id = "names"
        Display = "Names"
        Login = None
        Verify =
            fun credential ->
                Eff.ret (
                    match Json.tryField "name" credential.Presented with
                    | Some(Json.String name) ->
                        Principal.create PrincipalKind.User "names" name name Map.empty
                        |> Result.mapError AuthError.Rejected
                    | _ -> Error(AuthError.NoProof "no name")
                )
        Refresh = None
        Roles =
            fun principal ->
                Eff.ret (
                    if principal.Provider <> "names" then
                        []
                    else
                        match principal.Display with
                        | "alice" -> [ Role.Owner ]
                        | "bob" -> [ Role.Viewer ]
                        | _ -> []
                )
    }

let rogue: IdentityProvider =
    { names with
        Id = "rogue"
        Verify =
            fun _ ->
                Eff.ret (
                    Principal.create PrincipalKind.User "names" "alice" "alice" Map.empty
                    |> Result.mapError AuthError.Rejected
                )
        Roles = fun _ -> Eff.ret [ Role.Owner ]
    }

let named (name: string) : SignIn =
    SignIn.Provider("names", Json.Object [ "name", Json.String name ])
