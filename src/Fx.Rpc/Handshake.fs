namespace Fx.Rpc

open System
open Fx.Contracts

/// Who a peer is: a program name and version, for diagnostics.
type PeerInfo = { Name: string; Version: string }

/// A side's binary-channel settings: what it accepts from the other side.
type ChannelSettings =
    {
        /// Bytes the other side may send on a new channel before it gets credit.
        InitialWindow: int
        /// Largest payload of one data frame the other side may send.
        MaxFramePayload: int
        /// Most channels open at once, counting both directions.
        MaxChannels: int
    }

/// What a side accepts and offers, exchanged in `initialize`.
type Capabilities =
    {
        /// Largest JSON-RPC text frame (one message or batch, UTF-8 bytes) this side reads.
        MaxMessageSize: int
        /// Requests this side accepts in flight from the other side; beyond, it answers `Overloaded`.
        /// The other side waits for a free slot before sending. `None` if the side does not say.
        MaxPendingRequests: int option
        /// Binary channels; `None` if this side does not support them. Channels work only if both
        /// sides offer them.
        Channels: ChannelSettings option
        /// The methods and notifications this side handles, sorted. Extension methods
        /// (`_<namespace>/<name>`) are listed like any other.
        Methods: string list
    }

    override this.ToString() =
        let channels =
            match this.Channels with
            | Some c -> $"channels (window {c.InitialWindow}, frames {c.MaxFramePayload}, max {c.MaxChannels})"
            | None -> "no channels"

        let pending =
            match this.MaxPendingRequests with
            | Some n -> string n
            | None -> "unbounded"

        $"max message {this.MaxMessageSize}, {pending} pending, {channels}, {this.Methods.Length} methods"

/// `initialize` params, sent by the client. The object may carry further members (identity, …)
/// for the application, and `_meta`.
type InitializeParams =
    {
        /// The protocol versions the client speaks, preferred first.
        ProtocolVersions: string list
        Peer: PeerInfo
        Capabilities: Capabilities
    }

    override this.ToString() =
        let versions = String.Join(", ", this.ProtocolVersions)
        $"initialize {this.Peer.Name} {this.Peer.Version} (versions {versions}; {this.Capabilities})"

/// `initialize` result, sent by the server. May carry further members for the application.
type InitializeResult =
    {
        /// The version both sides now speak: the first of the client's that the server supports.
        ProtocolVersion: string
        Peer: PeerInfo
        Capabilities: Capabilities
    }

    override this.ToString() =
        $"initialized {this.Peer.Name} {this.Peer.Version} (version {this.ProtocolVersion}; {this.Capabilities})"

/// What a side learned about the other in the handshake.
type Remote =
    {
        ProtocolVersion: string
        Peer: PeerInfo
        Capabilities: Capabilities
        /// The whole `initialize` params (on the server) or result (on the client), including the
        /// members the application added; decode them with the application's own codec.
        Members: Json
    }

    override this.ToString() =
        $"{this.Peer.Name} {this.Peer.Version} (version {this.ProtocolVersion}; {this.Capabilities})"

module Handshake =
    /// The largest channel frame payload a side may ask for (16 MiB).
    let maxFramePayloadLimit = 16 * 1024 * 1024

    /// The smallest message size limit a side may announce (1 KiB): enough for any handshake.
    let minMessageSize = 1024

    let peerInfo: Codec<PeerInfo> =
        Codec.record {
            let! name = Codec.field "name" (fun (p: PeerInfo) -> p.Name) Codec.string
            and! version = Codec.field "version" (fun p -> p.Version) Codec.string
            return { Name = name; Version = version }
        }
        |> Codec.named "PeerInfo"

    let channelSettings: Codec<ChannelSettings> =
        Codec.record {
            let! window =
                Codec.field "initialWindow" (fun (c: ChannelSettings) -> c.InitialWindow) (Codec.int |> Codec.atLeast 1)

            and! frame =
                Codec.field
                    "maxFramePayload"
                    (fun c -> c.MaxFramePayload)
                    (Codec.int |> Codec.between 1 maxFramePayloadLimit)

            and! channels =
                Codec.field "maxChannels" (fun c -> c.MaxChannels) (Codec.int |> Codec.atLeast 0)

            return
                {
                    InitialWindow = window
                    MaxFramePayload = frame
                    MaxChannels = channels
                }
        }
        |> Codec.named "ChannelSettings"

    let capabilities: Codec<Capabilities> =
        Codec.record {
            let! maxMessageSize =
                Codec.field
                    "maxMessageSize"
                    (fun (c: Capabilities) -> c.MaxMessageSize)
                    (Codec.int |> Codec.atLeast minMessageSize)

            and! maxPending =
                Codec.optionalField "maxPendingRequests" (fun c -> c.MaxPendingRequests) (Codec.int |> Codec.atLeast 1)

            and! channels = Codec.optionalField "channels" (fun c -> c.Channels) channelSettings

            and! methods =
                Codec.defaultField "methods" (fun c -> c.Methods) [] (Codec.list Codec.string)

            return
                {
                    MaxMessageSize = maxMessageSize
                    MaxPendingRequests = maxPending
                    Channels = channels
                    Methods = methods
                }
        }
        |> Codec.named "Capabilities"

    let initializeParams: Codec<InitializeParams> =
        Codec.record {
            let! versions =
                Codec.field
                    "protocolVersions"
                    (fun (p: InitializeParams) -> p.ProtocolVersions)
                    (Codec.list Codec.string |> Codec.minItems 1)

            and! peer = Codec.field "peer" (fun p -> p.Peer) peerInfo
            and! caps = Codec.field "capabilities" (fun p -> p.Capabilities) capabilities

            return
                {
                    ProtocolVersions = versions
                    Peer = peer
                    Capabilities = caps
                }
        }
        |> Codec.named "InitializeParams"

    let initializeResult: Codec<InitializeResult> =
        Codec.record {
            let! version =
                Codec.field "protocolVersion" (fun (r: InitializeResult) -> r.ProtocolVersion) Codec.string

            and! peer = Codec.field "peer" (fun r -> r.Peer) peerInfo
            and! caps = Codec.field "capabilities" (fun r -> r.Capabilities) capabilities

            return
                {
                    ProtocolVersion = version
                    Peer = peer
                    Capabilities = caps
                }
        }
        |> Codec.named "InitializeResult"

    /// The first of the offered versions that is supported.
    let choose (offered: string list) (supported: string list) : string option =
        offered |> List.tryFind (fun v -> List.contains v supported)

    /// An object with more members appended. A member that is already there is a defect.
    let withMembers (members: (string * Json) list) (json: Json) : Json =
        match json with
        | Json.Object existing ->
            for name, _ in members do
                if existing |> List.exists (fun (key, _) -> key = name) then
                    invalidArg (nameof members) $"The handshake already has a member {Json.quote name}."

            Json.Object(existing @ members)
        | other -> invalidArg (nameof json) $"Expected an object, not {Json.kind other}."

    /// The error data of `UnsupportedVersion`: `{"supported": [...]}`.
    let unsupported (supported: string list) : RpcError =
        RpcError.create
            Protocol.ErrorCode.UnsupportedVersion
            ("no common protocol version; supported: " + String.Join(", ", supported))
        |> RpcError.withData (Json.Object [ "supported", Json.Array(supported |> List.map Json.String) ])
