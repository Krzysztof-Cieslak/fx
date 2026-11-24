/// Names, error codes and naming rules of the protocol (spec §17). Pure.
[<RequireQualifiedAccess>]
module Fx.Rpc.Protocol

open System
open Fx

/// The protocol versions this implementation speaks, preferred first. Peers agree on one in
/// `initialize`: the client offers its versions and the server picks the first it supports.
let versions = [ "1" ]

/// The value of every message's `jsonrpc` member.
[<Literal>]
let JsonRpcVersion = "2.0"

/// The handshake request every connection starts with (client to server).
[<Literal>]
let Initialize = "initialize"

/// The notification that cancels a request in flight: `{"id": <request id>}`. Either side sends it
/// for a request it made; the other side cancels the handler, which then answers `RequestCancelled`.
[<Literal>]
let CancelRequest = "$/cancelRequest"

/// Prefix of the protocol's own methods (`$/cancelRequest`). Applications cannot define them.
[<Literal>]
let ReservedPrefix = "$/"

/// The member that carries metadata in params and results (spec §17).
[<Literal>]
let MetaMember = "_meta"

/// An extension method in a namespace: `_<namespace>/<name>`, e.g. `_acme.dev/buffers/list`.
let extensionMethod (ns: string) (name: string) : string = "_" + ns + "/" + name

/// An extension method in the product's own namespace (`Product.protocolNamespace`).
let ownExtensionMethod (name: string) : string =
    extensionMethod Product.protocolNamespace name

/// A `_meta` key in the product's namespace: `<namespace>/<name>`. Other parties use their own
/// namespace (a reverse domain name is a good choice), so keys never collide.
let metaKey (name: string) : string = Product.protocolNamespace + "/" + name

/// The WebSocket subprotocol: `<namespace>.jsonrpc`. JSON-RPC messages travel in text messages and
/// channel frames in binary messages.
let webSocketSubprotocol = Product.protocolNamespace + ".jsonrpc"

/// Error codes. -32768 to -32000 are reserved by JSON-RPC 2.0; the protocol's own codes are in the
/// implementation-defined range -32099 to -32000.
module ErrorCode =
    /// The text is not JSON.
    [<Literal>]
    let ParseError = -32700

    /// The JSON is not a valid request object (or the batch is empty).
    [<Literal>]
    let InvalidRequest = -32600

    [<Literal>]
    let MethodNotFound = -32601

    /// The params do not decode with the method's codec; `data` holds the issues.
    [<Literal>]
    let InvalidParams = -32602

    /// The handler failed with a defect (an exception). The message never carries exception text.
    [<Literal>]
    let InternalError = -32603

    /// The method failed with one of its typed errors, an expected failure; `data` holds the error,
    /// encoded with the method's error codec, and `message` its text.
    [<Literal>]
    let ApplicationError = -32000

    /// The request was cancelled (`$/cancelRequest`, or the connection is closing).
    [<Literal>]
    let RequestCancelled = -32001

    /// A request other than `initialize` before the handshake completed.
    [<Literal>]
    let NotInitialized = -32002

    /// Too many requests in flight; retry later.
    [<Literal>]
    let Overloaded = -32003

    /// No protocol version in common; `data` is `{"supported": [...]}`.
    [<Literal>]
    let UnsupportedVersion = -32004

    /// The response would exceed the peer's message size limit.
    [<Literal>]
    let ResponseTooLarge = -32005

    /// The connection is closing and takes no new requests.
    [<Literal>]
    let ShuttingDown = -32006

    /// A short name for a code, for messages and logs.
    let name (code: int) : string =
        match code with
        | ParseError -> "parse error"
        | InvalidRequest -> "invalid request"
        | MethodNotFound -> "method not found"
        | InvalidParams -> "invalid params"
        | InternalError -> "internal error"
        | ApplicationError -> "application error"
        | RequestCancelled -> "request cancelled"
        | NotInitialized -> "not initialized"
        | Overloaded -> "overloaded"
        | UnsupportedVersion -> "unsupported version"
        | ResponseTooLarge -> "response too large"
        | ShuttingDown -> "shutting down"
        | code when code >= -32768 && code <= -32000 -> "reserved error"
        | _ -> "error"

/// Why a method name cannot be defined, or `None` if it can:
///
/// - not empty, and no control characters;
/// - `rpc.` is reserved by JSON-RPC 2.0, and `$/` by this protocol;
/// - an extension method is `_<namespace>/<name>`, with a non-empty namespace and name.
let checkMethodName (name: string) : string option =
    if String.IsNullOrEmpty name then
        Some "a method name must not be empty"
    elif name |> Seq.exists Char.IsControl then
        Some "a method name must not contain control characters"
    elif name.StartsWith("rpc.", StringComparison.Ordinal) then
        Some "method names starting with \"rpc.\" are reserved by JSON-RPC 2.0"
    elif name.StartsWith(ReservedPrefix, StringComparison.Ordinal) then
        Some "method names starting with \"$/\" are reserved by the protocol"
    elif name.StartsWith('_') then
        let slash = name.IndexOf '/'

        if slash <= 1 || slash = name.Length - 1 then
            Some "an extension method is named \"_<namespace>/<name>\""
        else
            None
    else
        None

/// Whether a method is an extension method (`_<namespace>/<name>`).
let isExtension (name: string) : bool = name.StartsWith('_')
