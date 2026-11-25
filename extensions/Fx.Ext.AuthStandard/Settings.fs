/// The settings of `api_key` and `env` in a provider's auth refs (`AuthMethodRef.Settings`): where the
/// key goes in a request, and where it comes from. Pure.
module Fx.Ext.AuthStandard.Settings

open System
open Fx.Contracts
open Fx.Effects
open Fx.Models
open Fx.Sdk

/// Where the key goes in a request.
[<RequireQualifiedAccess>]
type Placement =
    /// A header: `<name>: <scheme> <key>`, or the key alone without a scheme (Anthropic's
    /// `x-api-key`, OpenAI's `Authorization: Bearer`).
    | Header of name: string * scheme: string option
    /// A query parameter (`?key=…`).
    | Query of name: string

    override this.ToString() =
        match this with
        | Header(name, Some scheme) -> "header " + name + " (" + scheme + ")"
        | Header(name, None) -> "header " + name
        | Query name -> "query " + name

/// A method's settings, checked.
type KeySettings =
    {
        Placement: Placement
        /// `api_key`: a `secret:` reference the key comes from instead of a login (host config may
        /// name one; configuration never holds the key itself).
        Key: SecretRef option
        /// `env`: the host's environment variable that holds the key.
        Variable: string option
        /// `api_key`: what to tell the person who pastes the key.
        Instructions: string option
    }

    override this.ToString() =
        "key settings (" + string this.Placement + ")"

type private Raw =
    {
        Header: string option
        Scheme: string option
        Query: string option
        Key: string option
        Variable: string option
        Instructions: string option
        Credential: string option
    }

let private raw: Codec<Raw> =
    Codec.record {
        let! header = Codec.optionalField "header" (fun (r: Raw) -> r.Header) Codec.string
        and! scheme = Codec.optionalField "scheme" (fun r -> r.Scheme) Codec.string
        and! query = Codec.optionalField "query" (fun r -> r.Query) Codec.string
        and! key = Codec.optionalField "key" (fun r -> r.Key) Codec.string
        and! variable = Codec.optionalField "variable" (fun r -> r.Variable) Codec.string

        and! instructions =
            Codec.optionalField "instructions" (fun r -> r.Instructions) Codec.string

        and! credential =
            Codec.optionalField AuthMethod.CredentialSetting (fun r -> r.Credential) Codec.string

        return
            {
                Header = header
                Scheme = scheme
                Query = query
                Key = key
                Variable = variable
                Instructions = instructions
                Credential = credential
            }
    }
    |> Codec.strict

/// HTTP's token characters (RFC 9110): what header names and auth schemes are made of.
let isToken (text: string) : bool =
    not (String.IsNullOrEmpty text)
    && text
       |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || "!#$%&'*+-.^_`|~".Contains c)

let private isQueryName (text: string) : bool =
    not (String.IsNullOrEmpty text)
    && text
       |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '.' || c = '_' || c = '~' || c = '-')

/// The kinds these settings are for.
[<Literal>]
let ApiKey = "api_key"

[<Literal>]
let Env = "env"

/// Reads and checks a method's settings: exactly one of `header` (with an optional `scheme`) and
/// `query`; `api_key` may name a `key` (a `secret:` reference), a `credential` and `instructions`;
/// `env` must name its `variable`. Unknown members are errors (a typo would otherwise send no key).
let read (kind: string) (settings: Json) : Result<KeySettings, string> =
    let settings =
        match settings with
        | Json.Null -> Json.Object []
        | json -> json

    match Codec.decode raw settings with
    | Error issues -> Error(Issue.renderAtMost 5 issues)
    | Ok r ->
        let placement =
            match r.Header, r.Query with
            | Some _, Some _ -> Error "give either header or query, not both"
            | None, None -> Error "give header or query: where the key goes"
            | Some header, None when not (isToken header) -> Error("the header " + header + " is not a header name")
            | Some header, None ->
                match r.Scheme with
                | Some scheme when not (isToken scheme) -> Error("the scheme " + scheme + " is not an auth scheme")
                | scheme -> Ok(Placement.Header(header, scheme))
            | None, Some _ when r.Scheme.IsSome -> Error "a scheme goes with a header, not a query"
            | None, Some query when not (isQueryName query) -> Error("the query parameter " + query + " is not a name")
            | None, Some query -> Ok(Placement.Query query)

        let key =
            match r.Key with
            | None -> Ok None
            | Some _ when kind <> ApiKey -> Error("key is a setting of " + ApiKey)
            | Some text ->
                match SecretRef.tryParse text with
                | Ok reference -> Ok(Some reference)
                | Error e -> Error("key must be a secret: reference (configuration never holds keys): " + e)

        let variable =
            match kind, r.Variable with
            | Env, Some name when HostVariables.isName name -> Ok(Some name)
            | Env, Some name -> Error("the variable " + name + " is not an environment variable's name")
            | Env, None -> Error "variable is required: the environment variable that holds the key"
            | _, Some _ -> Error("variable is a setting of " + Env)
            | _, None -> Ok None

        let credential =
            match kind, r.Credential with
            | ApiKey, Some name -> CredentialId.tryCreate name |> Result.map ignore
            | _, Some _ -> Error(AuthMethod.CredentialSetting + " is a setting of " + ApiKey)
            | _, None -> Ok()

        let instructions =
            match kind, r.Instructions with
            | ApiKey, text -> Ok text
            | _, Some _ -> Error("instructions is a setting of " + ApiKey)
            | _, None -> Ok None

        match placement, key, variable, credential, instructions with
        | Ok placement, Ok key, Ok variable, Ok(), Ok instructions ->
            Ok
                {
                    Placement = placement
                    Key = key
                    Variable = variable
                    Instructions = instructions
                }
        | Error e, _, _, _, _
        | _, Error e, _, _, _
        | _, _, Error e, _, _
        | _, _, _, Error e, _
        | _, _, _, _, Error e -> Error e

/// A key as it may go into a request: printable ASCII without spaces, surrounding whitespace (a
/// pasted line's end) dropped. Anything else could split a header or a query.
let checkKey (value: string) : Result<string, string> =
    let key = value.Trim()

    if key.Length = 0 then
        Error "the key is empty"
    elif key |> Seq.exists (fun c -> c < '!' || c > '~') then
        Error "the key has characters other than printable ASCII without spaces"
    else
        Ok key

/// What to add to a request for a key.
let resolved (placement: Placement) (key: string) : ResolvedAuth =
    match placement with
    | Placement.Header(name, scheme) ->
        let value =
            match scheme with
            | Some scheme -> scheme + " " + key
            | None -> key

        {
            Headers = [ name, value ]
            Query = []
            Sign = None
        }
    | Placement.Query name ->
        {
            Headers = []
            Query = [ name, key ]
            Sign = None
        }
