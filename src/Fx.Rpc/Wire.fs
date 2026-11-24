namespace Fx.Rpc

open System
open Fx.Contracts

/// The id of a request: a string or an integer (JSON-RPC 2.0 also allows `null` and fractional
/// numbers; neither is accepted here, see `Wire.parse`). Responses carry the id of their request.
[<RequireQualifiedAccess>]
type RequestId =
    | Number of int64
    | String of string

    /// As JSON text: `7` or `"abc"`.
    override this.ToString() =
        match this with
        | Number n -> string n
        | String s -> Json.quote s

/// A JSON-RPC error object.
type RpcError =
    {
        Code: int
        Message: string
        Data: Json option
    }

    override this.ToString() = $"{this.Message} ({this.Code})"

module RpcError =
    let create (code: int) (message: string) : RpcError =
        {
            Code = code
            Message = message
            Data = None
        }

    let withData (data: Json) (error: RpcError) : RpcError = { error with Data = Some data }

    let parseError (detail: string) =
        create Protocol.ErrorCode.ParseError ("parse error: " + detail)

    let invalidRequest (detail: string) =
        create Protocol.ErrorCode.InvalidRequest ("invalid request: " + detail)

    let methodNotFound (name: string) =
        create Protocol.ErrorCode.MethodNotFound ("method not found: " + Json.quote name)

    /// Invalid params, with the decoder's issues in the message (at most 10) and in `data.issues`.
    let invalidParams (issues: Issue list) =
        create Protocol.ErrorCode.InvalidParams ("invalid params:\n" + Issue.renderAtMost 10 issues)
        |> withData (Json.Object [ "issues", Codec.encode (Codec.list Codecs.issue) issues ])

    /// An internal error. Only the exception's type is shown, never its message: messages may carry
    /// secrets or paths, and Native AOT changes some of them.
    let internalError (e: exn) =
        create Protocol.ErrorCode.InternalError ("internal error (" + e.GetType().Name + ")")

    let cancelled = create Protocol.ErrorCode.RequestCancelled "request cancelled"

    let notInitialized =
        create Protocol.ErrorCode.NotInitialized "the connection is not initialized"

    let overloaded (limit: int) =
        create Protocol.ErrorCode.Overloaded $"overloaded: more than {limit} requests in flight"

    let shuttingDown =
        create Protocol.ErrorCode.ShuttingDown "the connection is closing"

    let responseTooLarge (size: int) (limit: int) =
        create
            Protocol.ErrorCode.ResponseTooLarge
            $"the response has {size} bytes, more than the peer's limit of {limit}"

    /// The issues in an `InvalidParams` error's data, if it has any.
    let issues (error: RpcError) : Issue list =
        match error.Data |> Option.bind (Json.tryField "issues") with
        | Some json ->
            match Codec.decode (Codec.list Codecs.issue) json with
            | Ok issues -> issues
            | Error _ -> []
        | None -> []

/// Metadata attached to params and results as their `_meta` member (spec §17). Keys are
/// `<namespace>/<name>` (see `Protocol.metaKey`); receivers ignore keys they do not know.
type Meta = Map<string, Json>

module Meta =
    let empty: Meta = Map.empty

    let codec: Codec<Meta> = Codec.stringMap Codec.json

    /// Removes the `_meta` member from params or a result. Absent, it is empty; it must be an object.
    let split (value: Json option) : Result<Json option * Meta, Issue> =
        match value with
        | Some(Json.Object members) ->
            match members |> List.tryFind (fun (key, _) -> key = Protocol.MetaMember) with
            | None -> Ok(value, empty)
            | Some(_, json) ->
                let rest =
                    Json.Object(members |> List.filter (fun (key, _) -> key <> Protocol.MetaMember))

                match Codec.decode codec json with
                | Ok meta -> Ok(Some rest, meta)
                | Error _ -> Error(Issue.at [ PathSegment.Key Protocol.MetaMember ] "expected an object")
        | _ -> Ok(value, empty)

    /// Adds metadata as the `_meta` member. Absent params become an object; metadata cannot be
    /// attached to anything but an object (a defect).
    let attach (meta: Meta) (value: Json option) : Json option =
        if meta.IsEmpty then
            value
        else
            let entry = Protocol.MetaMember, Codec.encode codec meta

            match value with
            | None -> Some(Json.Object [ entry ])
            | Some(Json.Object members) ->
                if members |> List.exists (fun (key, _) -> key = Protocol.MetaMember) then
                    invalidArg (nameof value) "The value already has a _meta member."

                Some(Json.Object(members @ [ entry ]))
            | Some other -> invalidArg (nameof value) $"Metadata needs an object, not {Json.kind other}."

/// One JSON-RPC 2.0 message.
[<RequireQualifiedAccess>]
type Message =
    | Request of id: RequestId * method: string * parameters: Json option
    | Notification of method: string * parameters: Json option
    | Response of id: RequestId * result: Result<Json, RpcError>
    /// An error response with a `null` id: the peer could not read the id of something it received.
    | ErrorWithoutId of error: RpcError

    override this.ToString() =
        match this with
        | Request(id, name, _) -> $"request {id} {name}"
        | Notification(name, _) -> $"notification {name}"
        | Response(id, Ok _) -> $"response {id}"
        | Response(id, Error e) -> $"error response {id}: {e}"
        | ErrorWithoutId e -> $"error response: {e}"

/// One entry of a received text frame.
[<RequireQualifiedAccess>]
type Parsed =
    | Valid of Message
    /// Not a valid request; answer with this error, under the id if one could be read.
    | InvalidRequest of id: RequestId option * error: RpcError
    /// A malformed response. Responses are never answered, so it can only be dropped.
    | InvalidResponse of reason: string

    override this.ToString() =
        match this with
        | Valid m -> string m
        | InvalidRequest(_, e) -> string e
        | InvalidResponse reason -> "invalid response: " + reason

/// What one received text frame holds.
[<RequireQualifiedAccess>]
type Incoming =
    | Single of Parsed
    /// A JSON-RPC batch (a non-empty array).
    | Batch of Parsed list
    /// Not JSON, or an empty batch: answer with this error and a `null` id.
    | Unreadable of RpcError

    override this.ToString() =
        match this with
        | Single entry -> string entry
        | Batch entries -> $"batch of {entries.Length}"
        | Unreadable e -> string e

/// Reading and writing JSON-RPC 2.0 messages. Pure.
[<RequireQualifiedAccess>]
module Wire =

    let private at (path: string) (message: string) = path + ": " + message

    type private IdField =
        | NoId
        | NullId
        | Id of RequestId
        | BadId of string

    let private readId (json: Json) : IdField =
        match Json.tryField "id" json with
        | None -> NoId
        | Some Json.Null -> NullId
        | Some(Json.String s) -> Id(RequestId.String s)
        | Some(Json.Number n as value) ->
            match JsonNumber.tryToInt64 n with
            | Some i -> Id(RequestId.Number i)
            | None -> BadId(at "$.id" ("expected a string or an integer, got " + Json.describe value))
        | Some other -> BadId(at "$.id" ("expected a string or an integer, got " + Json.describe other))

    let private readError (json: Json) : Result<RpcError, string> =
        match json with
        | Json.Object _ ->
            let code =
                match Json.tryField "code" json with
                | Some(Json.Number n) -> JsonNumber.tryToInt n
                | _ -> None

            let message =
                match Json.tryField "message" json with
                | Some(Json.String s) -> Some s
                | _ -> None

            match code, message with
            | Some code, Some message ->
                Ok
                    {
                        Code = code
                        Message = message
                        Data = Json.tryField "data" json
                    }
            | None, _ -> Error(at "$.error.code" "expected an integer")
            | _, None -> Error(at "$.error.message" "expected a string")
        | other -> Error(at "$.error" ("expected an object, got " + Json.describe other))

    let private readParams (json: Json) : Result<Json option, string> =
        match Json.tryField "params" json with
        | None -> Ok None
        | Some(Json.Object _ as p)
        | Some(Json.Array _ as p) -> Ok(Some p)
        | Some other -> Error(at "$.params" ("expected an object or an array, got " + Json.describe other))

    let private readRequest (json: Json) (id: IdField) (name: Json) : Parsed =
        let known =
            match id with
            | Id id -> Some id
            | _ -> None

        let invalid detail =
            Parsed.InvalidRequest(known, RpcError.invalidRequest detail)

        match name, readParams json with
        | Json.String name, Ok parameters ->
            match id with
            | NoId -> Parsed.Valid(Message.Notification(name, parameters))
            | Id id -> Parsed.Valid(Message.Request(id, name, parameters))
            | NullId -> invalid (at "$.id" "null ids are not supported; use a string or an integer")
            | BadId detail -> invalid detail
        | Json.String _, Error detail -> invalid detail
        | other, _ -> invalid (at "$.method" ("expected a string, got " + Json.describe other))

    let private readResponse (json: Json) (id: IdField) : Parsed =
        let outcome =
            match Json.tryField "result" json, Json.tryField "error" json with
            | Some result, None -> Ok(Ok result)
            | None, Some error -> readError error |> Result.map Error
            | Some _, Some _ -> Error "a response has either \"result\" or \"error\", not both"
            | None, None -> Error "expected \"method\", \"result\" or \"error\""

        match outcome, id with
        | Error reason, Id id -> Parsed.InvalidResponse $"{reason} (id {id})"
        | Error reason, _ -> Parsed.InvalidResponse reason
        | Ok result, Id id -> Parsed.Valid(Message.Response(id, result))
        | Ok(Error e), NullId -> Parsed.Valid(Message.ErrorWithoutId e)
        | Ok(Ok _), NullId -> Parsed.InvalidResponse "a result needs the id of its request"
        | Ok _, NoId -> Parsed.InvalidResponse "a response needs an id"
        | Ok _, BadId detail -> Parsed.InvalidResponse detail

    /// Reads one message object.
    let readEntry (json: Json) : Parsed =
        match json with
        | Json.Object _ ->
            let id = readId json

            match Json.tryField "jsonrpc" json with
            | Some(Json.String Protocol.JsonRpcVersion) ->
                match Json.tryField "method" json with
                | Some name -> readRequest json id name
                | None ->
                    if
                        Json.tryField "result" json |> Option.isSome
                        || Json.tryField "error" json |> Option.isSome
                    then
                        readResponse json id
                    else
                        let known =
                            match id with
                            | Id id -> Some id
                            | _ -> None

                        Parsed.InvalidRequest(
                            known,
                            RpcError.invalidRequest
                                "expected \"method\" (a request) or \"result\"/\"error\" (a response)"
                        )
            | other ->
                let got =
                    match other with
                    | Some value -> Json.describe value
                    | None -> "nothing"

                let detail = at "$.jsonrpc" ("expected \"2.0\", got " + got)

                // Without a valid envelope, a response cannot be told from a request.
                if
                    Json.tryField "method" json |> Option.isNone
                    && Json.tryField "id" json |> Option.isSome
                then
                    Parsed.InvalidResponse detail
                else
                    match id with
                    | Id id -> Parsed.InvalidRequest(Some id, RpcError.invalidRequest detail)
                    | _ -> Parsed.InvalidRequest(None, RpcError.invalidRequest detail)
        | other ->
            Parsed.InvalidRequest(
                None,
                RpcError.invalidRequest ("expected a request object, got " + Json.describe other)
            )

    /// Reads a text frame: one message or a batch. Reading limits are those of `Json.parse`
    /// (depth 128, no duplicate keys).
    let parse (text: string) : Incoming =
        match Json.parse text with
        | Error e -> Incoming.Unreadable(RpcError.parseError (string e))
        | Ok(Json.Array []) -> Incoming.Unreadable(RpcError.invalidRequest "an empty batch")
        | Ok(Json.Array items) -> Incoming.Batch(items |> List.map readEntry)
        | Ok json -> Incoming.Single(readEntry json)

    let private idJson (id: RequestId) : Json =
        match id with
        | RequestId.Number n -> Json.int64 n
        | RequestId.String s -> Json.String s

    let private errorJson (error: RpcError) : Json =
        Json.Object(
            [ "code", Json.int error.Code; "message", Json.String error.Message ]
            @ (error.Data |> Option.map (fun d -> [ "data", d ]) |> Option.defaultValue [])
        )

    let private version = "jsonrpc", Json.String Protocol.JsonRpcVersion

    let private withParams (parameters: Json option) =
        parameters |> Option.map (fun p -> [ "params", p ]) |> Option.defaultValue []

    /// A message as JSON. Members in a fixed order: `jsonrpc`, `id`, `method`, `params` /
    /// `result` / `error`.
    let encode (message: Message) : Json =
        match message with
        | Message.Request(id, name, parameters) ->
            Json.Object([ version; "id", idJson id; "method", Json.String name ] @ withParams parameters)
        | Message.Notification(name, parameters) ->
            Json.Object([ version; "method", Json.String name ] @ withParams parameters)
        | Message.Response(id, Ok result) -> Json.Object [ version; "id", idJson id; "result", result ]
        | Message.Response(id, Error error) -> Json.Object [ version; "id", idJson id; "error", errorJson error ]
        | Message.ErrorWithoutId error -> Json.Object [ version; "id", Json.Null; "error", errorJson error ]

    /// A message as compact JSON text (no line breaks, so it is also one NDJSON line).
    let serialize (message: Message) : string = Json.serialize (encode message)

    /// A batch as compact JSON text.
    let serializeBatch (messages: Message list) : string =
        Json.serialize (Json.Array(messages |> List.map encode))
