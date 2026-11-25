namespace Fx.Auth

open System
open System.Threading
open System.Threading.Tasks
open Fx
open Fx.Effects
open Fx.Models
open Fx.Sdk

/// `auth` on the command line (spec §19.5 "fx auth …").
[<RequireQualifiedAccess>]
type AuthCommand =
    /// `auth login <provider> [--method <kind>]`: log in, storing the credential.
    | Login of provider: string * methodKind: string option
    /// `auth logout <provider>`: remove the provider's stored credentials.
    | Logout of provider: string
    /// `auth list`: the stored credentials, never their secrets.
    | List
    /// `auth status`: the store in use, why, and how each provider's credentials resolve.
    | Status

    override this.ToString() =
        match this with
        | Login(provider, Some kind) -> "login " + provider + " --method " + kind
        | Login(provider, None) -> "login " + provider
        | Logout provider -> "logout " + provider
        | List -> "list"
        | Status -> "status"

/// Runs a login's flow for the command line: shows a paste prompt and reads the pasted text without
/// echoing it, opens a browser, or shows a device code. The entry point builds it over the terminal
/// (T129); tests script it. A failure is what to tell the person.
type FlowRunner = AuthFlow -> CancellationToken -> Task<Result<FlowResult, string>>

[<RequireQualifiedAccess>]
module AuthCommand =
    /// The usage text, one line per command.
    let usage: string list =
        let bin = Product.binaryName

        [
            bin
            + " auth login <provider> [--method <kind>]   store a credential for a provider"
            bin
            + " auth logout <provider>                    remove a provider's stored credentials"
            bin
            + " auth list                                 list stored credentials (never their secrets)"
            bin
            + " auth status                               the credential store and each provider's credentials"
        ]

    /// Parses the arguments after `auth`.
    let parse (args: string list) : Result<AuthCommand, string> =
        match args with
        | [ "login"; provider ] when not (provider.StartsWith "-") -> Ok(AuthCommand.Login(provider, None))
        | [ "login"; provider; "--method"; kind ]
        | [ "login"; "--method"; kind; provider ] when not (provider.StartsWith "-") ->
            Ok(AuthCommand.Login(provider, Some kind))
        | [ "logout"; provider ] when not (provider.StartsWith "-") -> Ok(AuthCommand.Logout provider)
        | [ "list" ] -> Ok AuthCommand.List
        | [ "status" ] -> Ok AuthCommand.Status
        | "login" :: _ -> Error "usage: auth login <provider> [--method <kind>]"
        | "logout" :: _ -> Error "usage: auth logout <provider>"
        | [] -> Error "auth needs a command: login, logout, list or status"
        | command :: _ -> Error("unknown auth command " + command + ": use login, logout, list or status")

/// Runs `auth` commands against the host's service credentials: what the CLI's entry point calls.
/// Output never contains a secret.
[<RequireQualifiedAccess>]
module AuthCli =
    let private provider (providers: ProviderDef list) (id: string) =
        providers |> List.tryFind (fun p -> p.Id = id)

    let private known (providers: ProviderDef list) =
        match providers with
        | [] -> "none"
        | providers -> providers |> List.map _.Id |> String.concat ", "

    let private unknown (providers: ProviderDef list) (id: string) =
        1, [ "No provider is named " + id + " (providers: " + known providers + ")." ]

    let private storeName (service: AuthService) =
        "the " + service.Store.Kind + " store (" + service.Store.Protection + ")"

    /// Runs a command: the exit code and the lines to print. `runFlow` runs a login's flow.
    let run<'R when 'R :> IScope>
        (service: AuthService)
        (providers: ProviderDef list)
        (runFlow: FlowRunner)
        (command: AuthCommand)
        : Eff<'R, int * string list> =
        eff {
            match command with
            | AuthCommand.Login(id, kind) ->
                match provider providers id with
                | None -> return unknown providers id
                | Some def ->
                    match! AuthService.startLogin service def kind Map.empty None with
                    | Error e -> return 1, [ "Cannot log in: " + string e + "." ]
                    | Ok pending ->
                        let! flow =
                            match pending.Flow with
                            | None -> Eff.ret (Ok FlowResult.NoFlow)
                            | Some flow -> fun (env: 'R) -> runFlow flow env.Scope.Cancellation

                        match flow with
                        | Error e -> return 1, [ "Login cancelled: " + e + "." ]
                        | Ok result ->
                            match! AuthService.completeLogin service pending result with
                            | Error e -> return 1, [ "Cannot log in: " + string e + "." ]
                            | Ok outcome ->
                                return
                                    0,
                                    [
                                        "Stored the "
                                        + outcome.Method
                                        + " credential for "
                                        + outcome.Provider
                                        + " in "
                                        + storeName service
                                        + " as "
                                        + string outcome.Credential
                                        + "."
                                        yield!
                                            outcome.Notice |> Option.map (fun n -> "Note: " + n + ".") |> Option.toList
                                    ]
            | AuthCommand.Logout id ->
                match provider providers id with
                | None -> return unknown providers id
                | Some def ->
                    match! AuthService.logout service def with
                    | Error e -> return 1, [ "Cannot log out: " + string e + "." ]
                    | Ok [] -> return 0, [ "No stored credentials for " + id + " in " + storeName service + "." ]
                    | Ok removed ->
                        return
                            0,
                            [
                                "Removed "
                                + (removed |> List.map string |> String.concat ", ")
                                + " from "
                                + storeName service
                                + "."
                            ]
            | AuthCommand.List ->
                match! AuthService.list service with
                | Error e -> return 1, [ "Cannot list credentials: " + string e + "." ]
                | Ok [] -> return 0, [ "No credentials in " + storeName service + "." ]
                | Ok entries ->
                    let width = entries |> List.map (fun e -> e.Id.Name.Length) |> List.max

                    return
                        0,
                        [
                            "Credentials in " + storeName service + ":"
                            for entry in entries do
                                let facts =
                                    entry.Meta
                                    |> Map.toList
                                    |> List.map (fun (name, value) -> name + "=" + value)
                                    |> String.concat ", "

                                "  "
                                + entry.Id.Name.PadRight width
                                + "  "
                                + entry.Method
                                + (if facts = "" then "" else "  " + facts)
                        ]
            | AuthCommand.Status ->
                let! status = AuthService.status service providers

                let width =
                    status.Providers |> List.map (fun p -> p.Provider.Length) |> List.fold max 0

                return
                    0,
                    [
                        "Credential store: "
                        + status.Store
                        + " ("
                        + status.Protection
                        + "), "
                        + status.Reason
                        "Store status: " + string status.Probe
                        yield! status.Notice |> Option.map (fun n -> "Note: " + n + ".") |> Option.toList
                        for p in status.Providers do
                            match p.Method, p.Problem with
                            | Some kind, _ -> "  " + p.Provider.PadRight width + "  " + kind
                            | None, Some problem -> "  " + p.Provider.PadRight width + "  " + problem
                            | None, None -> "  " + p.Provider.PadRight width + "  -"
                    ]
        }
