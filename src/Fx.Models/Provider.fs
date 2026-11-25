namespace Fx.Models

open System
open System.Threading
open System.Threading.Tasks
open Fx.Contracts
open Fx.Effects

/// One edit of a JSON request body: set or remove the member at a path of member names.
[<RequireQualifiedAccess>]
type BodyEdit =
    | Set of path: string list * value: Json
    | Remove of path: string list

    override this.ToString() =
        match this with
        | Set(path, _) -> "set " + String.concat "." path
        | Remove path -> "remove " + String.concat "." path

/// Changes a provider makes to every request its adapter builds (spec §10.2 `Transform`): extra or
/// removed headers and query parameters, and edits of a JSON body. Data, so declarative provider
/// definitions (e.g. an OpenAI-compatible gateway) need no code.
type RequestTransform =
    {
        SetHeaders: (string * string) list
        RemoveHeaders: string list
        SetQuery: (string * string) list
        Body: BodyEdit list
    }

    override this.ToString() =
        $"transform({this.SetHeaders.Length} headers set, {this.RemoveHeaders.Length} removed, {this.SetQuery.Length} query, {this.Body.Length} body edits)"

/// How to authenticate to a provider: an auth method's kind and its settings (spec §11.3; the
/// methods are T121's).
type AuthMethodRef =
    {
        /// `api_key`, `env`, `oauth_pkce`, …
        Kind: string
        Settings: Json
    }

    override this.ToString() = "auth " + this.Kind

/// The models a provider offers: a bundled list, and where to discover more (T208).
type Catalog =
    {
        Models: ModelInfo list
        /// A path under the provider's endpoint that lists its models.
        Discover: string option
    }

    override this.ToString() = $"catalog({this.Models.Length} models)"

/// A model provider (spec §10.2): where its endpoints are, which models it has, how to authenticate,
/// and its quirks. Extensions contribute these as data (`ProviderDefs.contributionType` in Fx.Sdk).
type ProviderDef =
    {
        /// The provider part of `ModelRef`s.
        Id: string
        Name: string
        /// The base URL of each wire family it speaks, e.g. `anthropic.messages` →
        /// `https://api.anthropic.com/v1`. Requests may only go below these (`ProviderDef.allows`).
        Endpoints: (string * string) list
        Catalog: Catalog
        Auth: AuthMethodRef list
        /// Flags for adapters, by name (e.g. `maxCacheBreakpoints`), read with `ProviderDef.quirk`.
        Quirks: (string * Json) list
        Transform: RequestTransform option
    }

    override this.ToString() =
        $"provider {this.Id} ({this.Endpoints.Length} endpoints, {this.Catalog})"

/// What an auth method resolved for one request (spec §11.3): what to add to it, and optionally a
/// signature over the whole request (e.g. SigV4). Only the host holds it; it never prints its values.
[<NoEquality; NoComparison>]
type ResolvedAuth =
    {
        Headers: (string * string) list
        Query: (string * string) list
        Sign: (HttpRequest -> CancellationToken -> Task<HttpRequest>) option
    }

    override this.ToString() =
        let names = this.Headers |> List.map fst |> String.concat ", "
        $"auth(headers {names}; values redacted)"

module RequestTransform =
    let none: RequestTransform =
        {
            SetHeaders = []
            RemoveHeaders = []
            SetQuery = []
            Body = []
        }

    let private bodyEdit: Codec<BodyEdit> =
        Codec.union "kind" {
            let! set =
                Codec.case2
                    "set"
                    (fun path value -> BodyEdit.Set(path, value))
                    (Field.required "path" (Codec.list Codec.string |> Codec.minItems 1))
                    (Field.required "value" Codec.json)

            and! remove =
                Codec.case1
                    "remove"
                    BodyEdit.Remove
                    (Field.required "path" (Codec.list Codec.string |> Codec.minItems 1))

            return
                fun value ->
                    match value with
                    | BodyEdit.Set(path, v) -> set path v
                    | BodyEdit.Remove path -> remove path
        }

    let codec: Codec<RequestTransform> =
        Codec.record {
            let! setHeaders =
                Codec.defaultField
                    "setHeaders"
                    (fun (t: RequestTransform) -> t.SetHeaders)
                    []
                    (Codec.keyValues Codec.string)

            and! removeHeaders =
                Codec.defaultField "removeHeaders" (fun t -> t.RemoveHeaders) [] (Codec.list Codec.string)

            and! setQuery =
                Codec.defaultField "setQuery" (fun t -> t.SetQuery) [] (Codec.keyValues Codec.string)

            and! body = Codec.defaultField "body" (fun t -> t.Body) [] (Codec.list bodyEdit)

            return
                {
                    SetHeaders = setHeaders
                    RemoveHeaders = removeHeaders
                    SetQuery = setQuery
                    Body = body
                }
        }
        |> Codec.named "RequestTransform"

    let rec private setAt (path: string list) (value: Json) (json: Json) : Json =
        match path, json with
        | [], _ -> value
        | name :: rest, Json.Object members ->
            let current = members |> List.tryFind (fun (n, _) -> n = name) |> Option.map snd

            let updated = setAt rest value (current |> Option.defaultValue (Json.Object []))

            if current.IsSome then
                Json.Object(members |> List.map (fun (n, v) -> if n = name then n, updated else n, v))
            else
                Json.Object(members @ [ name, updated ])
        | name :: rest, _ -> Json.Object [ name, setAt rest value (Json.Object []) ]

    let rec private removeAt (path: string list) (json: Json) : Json =
        match path, json with
        | [ name ], Json.Object members -> Json.Object(members |> List.filter (fun (n, _) -> n <> name))
        | name :: rest, Json.Object members ->
            Json.Object(
                members
                |> List.map (fun (n, v) -> if n = name then n, removeAt rest v else n, v)
            )
        | _ -> json

    /// Applies the body edits to a JSON value.
    let editBody (edits: BodyEdit list) (json: Json) : Json =
        edits
        |> List.fold
            (fun json edit ->
                match edit with
                | BodyEdit.Set(path, value) -> setAt path value json
                | BodyEdit.Remove path -> removeAt path json)
            json

    let private withQuery (pairs: (string * string) list) (url: string) : string =
        match pairs with
        | [] -> url
        | pairs ->
            let encoded =
                pairs
                |> List.map (fun (k, v) -> Uri.EscapeDataString k + "=" + Uri.EscapeDataString v)
                |> String.concat "&"

            url + (if url.Contains '?' then "&" else "?") + encoded

    /// Adds query parameters to a URL.
    let addQuery (pairs: (string * string) list) (url: string) : string = withQuery pairs url

    let private utf8 = Text.UTF8Encoding(false)

    /// Applies the transform to a request. Body edits apply only to a body that is JSON.
    let apply (transform: RequestTransform) (request: HttpRequest) : HttpRequest =
        let headers =
            transform.RemoveHeaders
            |> List.fold (fun headers name -> HttpHeaders.remove name headers) request.Headers

        let headers =
            transform.SetHeaders
            |> List.fold (fun headers (name, value) -> HttpHeaders.set name value headers) headers

        let body =
            match transform.Body with
            | [] -> request.Body
            | edits ->
                match Json.parse (utf8.GetString request.Body) with
                | Ok json -> utf8.GetBytes(Json.serialize (editBody edits json))
                | Error _ -> request.Body

        { request with
            Url = withQuery transform.SetQuery request.Url
            Headers = headers
            Body = body
        }

module ProviderDef =
    let private authRef: Codec<AuthMethodRef> =
        Codec.record {
            let! kind =
                Codec.field "kind" (fun (a: AuthMethodRef) -> a.Kind) (Codec.string |> Codec.nonEmpty)

            and! settings =
                Codec.defaultField "settings" (fun a -> a.Settings) (Json.Object []) Codec.json

            return { Kind = kind; Settings = settings }
        }
        |> Codec.named "AuthMethodRef"

    let private catalog: Codec<Catalog> =
        Codec.record {
            let! models =
                Codec.defaultField "models" (fun (c: Catalog) -> c.Models) [] (Codec.list ModelInfo.codec)

            and! discover = Codec.optionalField "discover" (fun c -> c.Discover) Codec.string
            return { Models = models; Discover = discover }
        }
        |> Codec.named "Catalog"

    let private endpointUrl: Codec<string> =
        Codec.string
        |> Codec.refine (fun url ->
            match HttpRequest.tryUri url with
            | Some _ -> Ok()
            | None -> Error "expected an absolute http or https URL")

    let codec: Codec<ProviderDef> =
        Codec.record {
            let! id =
                Codec.field "id" (fun (p: ProviderDef) -> p.Id) (Codec.string |> Codec.pattern ModelRef.ProviderPattern)

            and! name = Codec.field "name" (fun p -> p.Name) Codec.string

            and! endpoints =
                Codec.field "endpoints" (fun p -> p.Endpoints) (Codec.keyValues endpointUrl)

            and! catalog =
                Codec.defaultField "catalog" (fun p -> p.Catalog) { Models = []; Discover = None } catalog

            and! auth = Codec.defaultField "auth" (fun p -> p.Auth) [] (Codec.list authRef)

            and! quirks =
                Codec.defaultField "quirks" (fun p -> p.Quirks) [] (Codec.keyValues Codec.json)

            and! transform =
                Codec.optionalField "transform" (fun p -> p.Transform) RequestTransform.codec

            return
                {
                    Id = id
                    Name = name
                    Endpoints = endpoints
                    Catalog = catalog
                    Auth = auth
                    Quirks = quirks
                    Transform = transform
                }
        }
        |> Codec.describe "A model provider: endpoints per wire family, catalog, auth methods and quirks."
        |> Codec.named "ProviderDef"

    /// A provider with one endpoint and nothing else.
    let create (id: string) (family: string) (endpoint: string) : ProviderDef =
        {
            Id = id
            Name = id
            Endpoints = [ family, endpoint ]
            Catalog = { Models = []; Discover = None }
            Auth = []
            Quirks = []
            Transform = None
        }

    /// A quirk's value, if the provider has it.
    let quirk (name: string) (provider: ProviderDef) : Json option =
        provider.Quirks |> List.tryFind (fun (n, _) -> n = name) |> Option.map snd

    /// The base URL for a wire family.
    let endpoint (family: string) (provider: ProviderDef) : string option =
        provider.Endpoints |> List.tryFind (fun (f, _) -> f = family) |> Option.map snd

    /// The catalog entry of a model.
    let model (reference: ModelRef) (provider: ProviderDef) : ModelInfo option =
        provider.Catalog.Models |> List.tryFind (fun m -> m.Ref = reference)

    /// Whether a URL is below one of the provider's endpoints (same scheme, host and port; path
    /// under the endpoint's path).
    let allows (url: string) (provider: ProviderDef) : bool =
        match HttpRequest.tryUri url with
        | None -> false
        | Some target ->
            provider.Endpoints
            |> List.exists (fun (_, base') ->
                match HttpRequest.tryUri base' with
                | None -> false
                | Some root ->
                    let rootPath = root.AbsolutePath.TrimEnd '/'

                    root.Scheme = target.Scheme
                    && String.Equals(root.Host, target.Host, StringComparison.OrdinalIgnoreCase)
                    && root.Port = target.Port
                    && (target.AbsolutePath = rootPath
                        || target.AbsolutePath.StartsWith(rootPath + "/", StringComparison.Ordinal)))

module ResolvedAuth =
    let none: ResolvedAuth =
        {
            Headers = []
            Query = []
            Sign = None
        }

    /// Applies the auth to a request: sets its headers, adds its query, then signs.
    let apply (auth: ResolvedAuth) (request: HttpRequest) (cancellation: CancellationToken) : Task<HttpRequest> =
        let request =
            { request with
                Url = RequestTransform.addQuery auth.Query request.Url
                Headers =
                    auth.Headers
                    |> List.fold (fun headers (name, value) -> HttpHeaders.set name value headers) request.Headers
            }

        match auth.Sign with
        | Some sign -> sign request cancellation
        | None -> Task.FromResult request

/// Maps what providers answer to typed errors: shared by adapters for the cases their own error
/// bodies do not settle.
[<RequireQualifiedAccess>]
module ErrorMapping =
    /// How long the provider asks to wait: `retry-after-ms`, or `retry-after` in seconds.
    let retryAfter (headers: (string * string) list) : TimeSpan option =
        let number (text: string) =
            match
                Double.TryParse(
                    text.Trim(),
                    Globalization.NumberStyles.Float,
                    Globalization.CultureInfo.InvariantCulture
                )
            with
            | true, n when n >= 0.0 && not (Double.IsInfinity n) -> Some n
            | _ -> None

        match HttpHeaders.tryFind "retry-after-ms" headers |> Option.bind number with
        | Some ms -> Some(TimeSpan.FromMilliseconds ms)
        | None ->
            HttpHeaders.tryFind "retry-after" headers
            |> Option.bind number
            |> Option.map TimeSpan.FromSeconds

    /// The error for an HTTP error status, when the body says nothing more specific.
    let ofStatus (status: int) (headers: (string * string) list) (message: string) : ChatError =
        let later =
            {
                RetryAfter = retryAfter headers
                Message = message
            }

        match status with
        | 401 -> ChatError.AuthFailed message
        | 402 -> ChatError.QuotaExhausted message
        | 403 -> ChatError.PermissionDenied message
        | 404 -> ChatError.NotFound message
        | 408 -> ChatError.Transport(HttpError.TimedOut HttpPhase.Response)
        | 429 -> ChatError.RateLimited later
        | 503
        | 529 -> ChatError.Overloaded later
        | status when status >= 500 -> ChatError.ServerError(status, message)
        | _ -> ChatError.InvalidRequest message

    /// The error for a transport failure.
    let ofTransport (error: HttpError) : ChatError = ChatError.Transport error
