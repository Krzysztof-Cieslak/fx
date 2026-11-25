/// The extension protocol (spec §16.1, §17): how the host talks to an extension process over
/// JSON-RPC (`src/Fx.Rpc`), in any language. The host is the client: it starts the process, sends
/// `initialize` with `extensionProtocol`, then activates extensions, runs their contributions' code
/// and calls their methods. One process may serve several extensions, and load more with a runtime
/// it contributes (e.g. `bun`), so every request names the extension.
module Fx.Extensions.ExtensionProtocol

open Fx.Contracts
open Fx.Rpc
open Fx.Sdk

/// The protocol versions this host speaks.
let versions = [ Versions.Protocol ]

/// The `initialize` member that carries the extension protocol version (client to server).
[<Literal>]
let VersionMember = "extensionProtocol"

/// The `initialize` result member listing the extensions a process serves.
[<Literal>]
let ServesMember = "serves"

/// The typed errors of the protocol's methods. Four cases at most: `Codec.union` with more fails the
/// Native AOT compilation (IL3054); the host knows which method it called, so it tells an unknown or
/// invalid method from an unknown or invalid contribution.
[<RequireQualifiedAccess>]
type ProtocolError =
    /// No such extension, contribution or method: `extension <id>`, `contribution <address>`,
    /// `method <name>`.
    | Unknown of what: string
    /// The input or params do not decode with the contribution type's or the method's codec.
    | Invalid of issues: Issue list
    /// The method failed with one of its typed errors.
    | MethodFailed of message: string * data: Json
    /// The extension is not active, the contribution's code cannot leave its process (interpreter
    /// middleware), or activation failed.
    | Failed of reason: string

    override this.ToString() =
        match this with
        | Unknown what -> "unknown " + what
        | Invalid issues -> "invalid:\n" + Issue.renderAtMost 10 issues
        | MethodFailed(message, _) -> message
        | Failed reason -> reason

type InvokeParams =
    {
        Extension: string
        Type: string
        Id: string
        Input: Json
    }

    override this.ToString() =
        $"invoke {this.Extension}/{this.Type}/{this.Id}"

type CallParams =
    {
        Extension: string
        Method: string
        Params: Json
    }

    override this.ToString() = $"call {this.Extension} {this.Method}"

type ExtensionRef =
    {
        Extension: string
    }

    override this.ToString() = this.Extension

module Codecs =
    let private issues = Codec.list Codecs.issue

    let protocolError: Codec<ProtocolError> =
        Codec.union "kind" {
            let! unknown =
                Codec.case1 "unknown" ProtocolError.Unknown (Field.required "what" Codec.string)

            and! invalid =
                Codec.case1 "invalid" ProtocolError.Invalid (Field.required "issues" issues)

            and! methodFailed =
                Codec.case2
                    "methodFailed"
                    (fun message data -> ProtocolError.MethodFailed(message, data))
                    (Field.required "message" Codec.string)
                    (Field.required "data" Codec.json)

            and! failed =
                Codec.case1 "failed" ProtocolError.Failed (Field.required "reason" Codec.string)

            return
                fun value ->
                    match value with
                    | ProtocolError.Unknown what -> unknown what
                    | ProtocolError.Invalid issues -> invalid issues
                    | ProtocolError.MethodFailed(message, data) -> methodFailed message data
                    | ProtocolError.Failed reason -> failed reason
        }
        |> Codec.named "ExtensionProtocolError"

    let activateParams: Codec<ActivateParams> =
        Codec.record {
            let! extension =
                Codec.field "extension" (fun (p: ActivateParams) -> p.Extension) Codec.string

            and! manifest = Codec.optionalField "manifest" (fun p -> p.Manifest) Manifest.codec
            and! root = Codec.optionalField "root" (fun p -> p.Root) Codec.string

            return
                {
                    Extension = extension
                    Manifest = manifest
                    Root = root
                }
        }
        |> Codec.named "ActivateParams"

    let activated: Codec<Activated> =
        Codec.record {
            let! manifest =
                Codec.field "manifest" (fun (a: Activated) -> a.Manifest) Manifest.codec

            and! contributions =
                Codec.field "contributions" (fun a -> a.Contributions) (Codec.list Manifest.contributionDecl)

            and! methods = Codec.field "methods" (fun a -> a.Methods) (Codec.list Codec.string)

            return
                {
                    Manifest = manifest
                    Contributions = contributions
                    Methods = methods
                }
        }
        |> Codec.named "Activated"

    let invokeParams: Codec<InvokeParams> =
        Codec.record {
            let! extension =
                Codec.field "extension" (fun (p: InvokeParams) -> p.Extension) Codec.string

            and! kind = Codec.field "type" (fun p -> p.Type) Codec.string
            and! id = Codec.field "id" (fun p -> p.Id) Codec.string
            and! input = Codec.field "input" (fun p -> p.Input) Codec.json

            return
                {
                    Extension = extension
                    Type = kind
                    Id = id
                    Input = input
                }
        }
        |> Codec.named "InvokeParams"

    /// `{"output": …}`: results are wrapped, so `null` and scalars travel as results too.
    let output: Codec<Json> =
        Codec.record {
            let! output = Codec.field "output" id Codec.json
            return output
        }

    let callParams: Codec<CallParams> =
        Codec.record {
            let! extension =
                Codec.field "extension" (fun (p: CallParams) -> p.Extension) Codec.string

            and! name = Codec.field "method" (fun p -> p.Method) Codec.string
            and! parameters = Codec.field "params" (fun p -> p.Params) Codec.json

            return
                {
                    Extension = extension
                    Method = name
                    Params = parameters
                }
        }
        |> Codec.named "CallParams"

    let extensionRef: Codec<ExtensionRef> =
        Codec.record {
            let! extension =
                Codec.field "extension" (fun (r: ExtensionRef) -> r.Extension) Codec.string

            return { Extension = extension }
        }
        |> Codec.named "ExtensionRef"

/// Activates an extension the process serves, or loads a package with a runtime it contributes.
let activate =
    Method.define "extension/activate" Codecs.activateParams Codecs.activated
    |> Method.withErrors Codecs.protocolError

/// Runs a contribution's code: a gate's check, a transform, an observer.
let invoke =
    Method.define "extension/invoke" Codecs.invokeParams Codecs.output
    |> Method.withErrors Codecs.protocolError

/// Calls a method of an extension's RPC contract.
let call =
    Method.define "extension/call" Codecs.callParams Codecs.output
    |> Method.withErrors Codecs.protocolError

/// Deactivates an extension.
let deactivate =
    Method.define "extension/deactivate" Codecs.extensionRef Codec.unit
    |> Method.withErrors Codecs.protocolError
