namespace Fx.Effects

open System
open System.Threading
open System.Threading.Tasks

/// A reference to a secret, written `secret:<name>` in configuration (spec §11, §18). Configuration
/// never holds secret values, only references, and the host resolves them.
[<Struct>]
type SecretRef =
    private
    | SecretRef of string

    /// The name after `secret:`.
    member this.Name =
        let (SecretRef name) = this
        name

    override this.ToString() = "secret:" + this.Name

module SecretRef =
    [<Literal>]
    let Prefix = "secret:"

    let private isNameChar (c: char) =
        Char.IsAsciiLetterOrDigit c || c = '.' || c = '_' || c = '-' || c = '/'

    /// A reference to the named secret. Names are 1–128 characters from `A-Z a-z 0-9 . _ - /` and
    /// start with a letter or digit.
    let tryCreate (name: string) : Result<SecretRef, string> =
        if String.IsNullOrEmpty name || name.Length > 128 then
            Error "a secret name has 1 to 128 characters"
        elif not (Char.IsAsciiLetterOrDigit name[0]) then
            Error "a secret name starts with a letter or digit"
        elif not (Seq.forall isNameChar name) then
            Error "a secret name has only letters, digits and . _ - /"
        else
            Ok(SecretRef name)

    /// Parses `secret:<name>`.
    let tryParse (text: string) : Result<SecretRef, string> =
        if not (text.StartsWith(Prefix, StringComparison.Ordinal)) then
            Error $"a secret reference starts with '{Prefix}'"
        else
            tryCreate (text.Substring Prefix.Length)

/// A secret's text: a key, a token, what a keyring or a credential store holds. It never prints its
/// value (`ToString` and string interpolation show only `(redacted)`), so it does not leak into logs,
/// traces or prompts by accident. Two are equal when their texts are (ordinal); keyrings store it as
/// UTF-8.
[<Sealed>]
type SecretText(value: string) =
    /// The secret itself. Call it at the one place that needs the value.
    member _.Reveal() = value

    override _.ToString() = "(redacted)"

    override _.Equals(other: obj) =
        match other with
        | :? SecretText as other -> String.Equals(value, other.Reveal(), StringComparison.Ordinal)
        | _ -> false

    override _.GetHashCode() =
        StringComparer.Ordinal.GetHashCode value

    interface IEquatable<SecretText> with
        member _.Equals(other: SecretText | null) =
            match other with
            | null -> false
            | other -> String.Equals(value, other.Reveal(), StringComparison.Ordinal)

/// A resolved secret value. It never prints its value (`ToString` and string interpolation show
/// only the reference), so it does not leak into logs, traces or prompts by accident. Call `Reveal`
/// at the one place that needs the value, e.g. building an HTTP header.
[<Sealed>]
type Secret(reference: SecretRef, value: string) =
    member _.Reference = reference

    member _.Reveal() = value

    override _.ToString() = $"{reference} (redacted)"

/// A secret that could not be resolved (an expected failure).
type SecretError =
    | SecretNotFound of SecretRef
    /// The store holding it is unavailable (e.g. a locked keychain); the reason is for display.
    | SecretUnavailable of SecretRef * reason: string

    override this.ToString() =
        match this with
        | SecretNotFound reference -> $"{reference} not found"
        | SecretUnavailable(reference, reason) -> $"{reference} unavailable: {reason}"

/// The secrets capability: resolves references. Host-only: environments never get it (spec §11.5).
///
/// Interpreters: `InMemorySecrets` (Fx.Effects.Fakes); credential stores and secret sources are T121.
/// Resolved values must never be journaled; a recording interpreter records the reference and the
/// outcome only.
type Secrets =
    {
        Resolve: SecretRef -> CancellationToken -> Task<Result<Secret, SecretError>>
    }

    override _.ToString() = "Secrets"

/// An environment that can resolve secrets.
type ISecrets =
    abstract Secrets: Secrets

module Secrets =
    let resolve<'R when 'R :> ISecrets and 'R :> IScope> (reference: SecretRef) : Eff<'R, Result<Secret, SecretError>> =
        fun env -> env.Secrets.Resolve reference env.Scope.Cancellation
