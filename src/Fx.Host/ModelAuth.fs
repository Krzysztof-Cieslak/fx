namespace Fx.Host

open System
open System.Threading
open System.Threading.Tasks
open Fx.Contracts
open Fx.Models

/// Provider credentials until T121 brings auth methods, credential stores and `secret:` references
/// (spec §11.3–11.4). This is the seam T121 replaces: `ModelAccess.Auth`. For now the host understands
/// one method of a provider's `Auth` list, `env` (`{"variable", "header", "scheme"?}`: the key from a
/// variable of the host's process, e.g. `ANTHROPIC_API_KEY`), read from the variables the host read once
/// at startup. Credentials stay on the host: environments never see them (`HostEnvironment.scrub`).
[<RequireQualifiedAccess>]
module ModelAuth =
    /// An `env` method's settings: the variable, the header, and the scheme before the value.
    type EnvMethod =
        {
            Variable: string
            Header: string
            Scheme: string option
        }

        override this.ToString() = $"env {this.Variable} → {this.Header}"

    let private envMethod: Codec<EnvMethod> =
        Codec.record {
            let! variable =
                Codec.field "variable" (fun (m: EnvMethod) -> m.Variable) (Codec.string |> Codec.nonEmpty)

            and! header =
                Codec.field "header" (fun (m: EnvMethod) -> m.Header) (Codec.string |> Codec.nonEmpty)

            and! scheme =
                Codec.optionalField "scheme" (fun (m: EnvMethod) -> m.Scheme) Codec.string

            return
                {
                    Variable = variable
                    Header = header
                    Scheme = scheme
                }
        }

    /// The `env` methods a provider declares, in order.
    let envMethods (provider: ProviderDef) : EnvMethod list =
        provider.Auth
        |> List.choose (fun method ->
            if method.Kind = "env" then
                Codec.decode envMethod method.Settings |> Result.toOption
            else
                None)

    /// The credentials of a provider from the host's variables: the first `env` method whose variable
    /// is set; `AuthFailed` naming the variables otherwise.
    let resolve (variables: (string * string) list) (provider: ProviderDef) : Result<ResolvedAuth, ChatError> =
        let value (name: string) =
            variables
            |> List.tryFind (fun (n, _) ->
                if OperatingSystem.IsWindows() then
                    String.Equals(n, name, StringComparison.OrdinalIgnoreCase)
                else
                    n = name)
            |> Option.map snd
            |> Option.filter (fun v -> v.Trim() <> "")

        let methods = envMethods provider

        match
            methods
            |> List.tryPick (fun m -> value m.Variable |> Option.map (fun v -> m, v.Trim()))
        with
        | Some(m, key) ->
            let header =
                match m.Scheme with
                | Some scheme -> scheme + " " + key
                | None -> key

            Ok
                {
                    Headers = [ m.Header, header ]
                    Query = []
                    Sign = None
                }
        | None when methods.IsEmpty ->
            Error(ChatError.AuthFailed $"no credentials for {provider.Id}: it has no environment-variable method yet")
        | None ->
            let names = methods |> List.map _.Variable |> String.concat " or "
            Error(ChatError.AuthFailed $"no credentials for {provider.Id}: set {names}")

    /// `ModelAccess.Auth` over the host's variables.
    let ofVariables
        (variables: (string * string) list)
        : ProviderDef -> CancellationToken -> Task<Result<ResolvedAuth, ChatError>> =
        fun provider _ -> Task.FromResult(resolve variables provider)
