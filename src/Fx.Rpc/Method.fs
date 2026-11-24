namespace Fx.Rpc

open System
open Fx.Contracts

/// The error type of methods that have no typed errors. It has no values.
[<Sealed>]
type NoError private () =
    override _.ToString() = "no error"

    /// Encodes nothing (there is nothing to encode) and decodes nothing.
    static member Codec: Codec<NoError> =
        Codec.create
            (Schema.ofKind (SchemaKind.AnyOf [])
             |> Schema.describe "This method has no typed errors.")
            (fun _ -> Json.Null)
            (fun _ -> Error [ Issue.create "this method has no typed errors" ])

/// A request type: its name and the codecs of its params, result and typed errors. One value builds
/// both the client stub (`Rpc.call`) and the server handler (`Handler.request`), so the two sides
/// cannot disagree.
///
/// - Params encode to a JSON object (or to `null` with `Codec.unit`, and then are left out). The
///   `_meta` member is reserved.
/// - Errors are the method's expected failures (`Result.Error` in the handler); they travel as
///   `ApplicationError` with the error in `data` and its `ToString()` as the message.
type Method<'P, 'R, 'E> =
    {
        Name: string
        Params: Codec<'P>
        Result: Codec<'R>
        Error: Codec<'E>
    }

    override this.ToString() = this.Name

/// A notification type: its name and the codec of its params.
type Notification<'P> =
    {
        Name: string
        Params: Codec<'P>
    }

    override this.ToString() = this.Name

/// A result or typed value with its `_meta`.
type Reply<'T> =
    {
        Value: 'T
        Meta: Meta
    }

    override this.ToString() =
        $"{this.Value} (meta: {this.Meta.Count} keys)"

module Reply =
    let ofValue (value: 'T) : Reply<'T> = { Value = value; Meta = Meta.empty }

/// Why a connection ended.
[<RequireQualifiedAccess>]
type CloseReason =
    /// This side closed it.
    | Local
    /// The peer ended the connection (end of input, or its WebSocket close).
    | Peer
    /// The transport failed (reset, broken pipe).
    | TransportFailed of reason: string
    /// The peer broke the protocol: oversized or malformed frames, flow-control violations.
    | ProtocolViolation of reason: string
    /// The handshake did not complete.
    | HandshakeFailed of reason: string

    override this.ToString() =
        match this with
        | Local -> "closed locally"
        | Peer -> "closed by the peer"
        | TransportFailed reason -> "transport failed: " + reason
        | ProtocolViolation reason -> "protocol violation: " + reason
        | HandshakeFailed reason -> "handshake failed: " + reason

/// Why a call returned no result.
[<RequireQualifiedAccess>]
type CallError<'E> =
    /// The method failed with one of its typed errors: an expected failure.
    | Failed of 'E
    /// The peer answered with a protocol-level error: unknown method, invalid params (with issues),
    /// an internal error, overload, cancellation, …
    | Rejected of RpcError
    /// The result or the typed error does not decode with the method's codecs (a peer bug).
    | InvalidReply of Issue list
    /// The request is larger than the peer accepts; it was not sent.
    | TooLarge of size: int * limit: int
    /// No answer in time; the request was cancelled at the peer.
    | TimedOut of after: TimeSpan
    /// The connection ended before the answer arrived.
    | Disconnected of CloseReason

    override this.ToString() =
        match this with
        | Failed e -> string e
        | Rejected e -> string e
        | InvalidReply issues -> "invalid reply:\n" + Issue.renderAtMost 10 issues
        | TooLarge(size, limit) -> $"the request has {size} bytes, more than the peer's limit of {limit}"
        | TimedOut after -> $"no answer after {after.TotalSeconds} s"
        | Disconnected reason -> "disconnected: " + string reason

/// Why a notification was not sent.
[<RequireQualifiedAccess>]
type SendError =
    | TooLarge of size: int * limit: int
    | Disconnected of CloseReason

    override this.ToString() =
        match this with
        | TooLarge(size, limit) -> $"the message has {size} bytes, more than the peer's limit of {limit}"
        | Disconnected reason -> "disconnected: " + string reason

module internal Params =
    let rec acceptsNull (schema: Schema) : bool =
        match schema.Kind with
        | SchemaKind.Null
        | SchemaKind.Nullable _
        | SchemaKind.Any -> true
        | SchemaKind.Named named -> acceptsNull named.Body
        | SchemaKind.AnyOf schemas -> schemas |> List.exists acceptsNull
        | _ -> false

    /// Params as sent: `null` is left out; anything but an object or an array is a defect.
    let encode (codec: Codec<'P>) (value: 'P) : Json option =
        match codec.Encode value with
        | Json.Null -> None
        | Json.Object _
        | Json.Array _ as json -> Some json
        | other ->
            invalidArg (nameof value) $"Params must encode to an object, an array or null, not {Json.kind other}."

    /// Decodes params; absent params read as `null` if the codec accepts it, otherwise as `{}`, so
    /// records whose fields are all optional accept absent params.
    let decode (codec: Codec<'P>) (json: Json option) : Validation<'P, Issue> =
        let json =
            match json with
            | Some json -> json
            | None when acceptsNull codec.Schema -> Json.Null
            | None -> Json.Object []

        codec.Decode json

module Method =
    let private check (name: string) =
        match Protocol.checkMethodName name with
        | Some problem -> invalidArg (nameof name) $"Invalid method name {Json.quote name}: {problem}."
        | None -> ()

    /// A method without typed errors.
    let define (name: string) (parameters: Codec<'P>) (result: Codec<'R>) : Method<'P, 'R, NoError> =
        check name

        {
            Name = name
            Params = parameters
            Result = result
            Error = NoError.Codec
        }

    /// Gives a method typed errors.
    let withErrors (error: Codec<'E>) (m: Method<'P, 'R, _>) : Method<'P, 'R, 'E> =
        {
            Name = m.Name
            Params = m.Params
            Result = m.Result
            Error = error
        }

module Notification =
    let define (name: string) (parameters: Codec<'P>) : Notification<'P> =
        match Protocol.checkMethodName name with
        | Some problem -> invalidArg (nameof name) $"Invalid notification name {Json.quote name}: {problem}."
        | None -> { Name = name; Params = parameters }
