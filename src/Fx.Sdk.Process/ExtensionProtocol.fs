/// The extension protocol (spec §16.1, §17): how the host talks to an extension process over
/// JSON-RPC (`src/Fx.Rpc`), in any language. The host is the client: it starts the process, sends
/// `initialize` with `extensionProtocol`, then activates extensions, runs their contributions' code
/// and calls their methods. One process may serve several extensions, and load more with a runtime
/// it contributes (e.g. `bun`), so every request names the extension. Both sides use these values:
/// the host (`Fx.Extensions`) and the servers (`ExtensionServer` here, `@fx/sdk` in TypeScript, whose
/// types are generated from these codecs).
module Fx.Sdk.Process.ExtensionProtocol

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

/// The typed errors of the protocol's methods. Contributions are addressed as
/// `<extension>/<type>/<id>`. The host knows which method it called, so `Invalid` is the input of a
/// contribution or the params of a method.
[<RequireQualifiedAccess>]
type ProtocolError =
    /// The process neither serves this extension nor can load it.
    | UnknownExtension of extension: string
    /// The extension is not active: never activated, or deactivated.
    | NotActive of extension: string
    /// The process has no runtime for the package, or its runtime refused it.
    | ActivationFailed of extension: string * reason: string
    /// The active extension has no such contribution.
    | UnknownContribution of address: string
    /// The contribution's code cannot leave its process (interpreter middleware).
    | InProcessOnly of address: string
    /// The active extension has no such method.
    | UnknownMethod of name: string
    /// The input or params do not decode with the contribution type's or the method's codec.
    | Invalid of issues: Issue list
    /// The method failed with one of its typed errors.
    | MethodFailed of message: string * data: Json

    override this.ToString() =
        match this with
        | UnknownExtension extension -> "unknown extension " + extension
        | NotActive extension -> extension + " is not active"
        | ActivationFailed(_, reason) -> reason
        | UnknownContribution address -> "unknown contribution " + address
        | InProcessOnly address -> address + " runs only in its process"
        | UnknownMethod name -> "unknown method " + name
        | Invalid issues -> "invalid:\n" + Issue.renderAtMost 10 issues
        | MethodFailed(message, _) -> message

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
            let! unknownExtension =
                Codec.case1 "unknownExtension" ProtocolError.UnknownExtension (Field.required "extension" Codec.string)

            and! notActive =
                Codec.case1 "notActive" ProtocolError.NotActive (Field.required "extension" Codec.string)

            and! activationFailed =
                Codec.case2
                    "activationFailed"
                    (fun extension reason -> ProtocolError.ActivationFailed(extension, reason))
                    (Field.required "extension" Codec.string)
                    (Field.required "reason" Codec.string)

            and! unknownContribution =
                Codec.case1
                    "unknownContribution"
                    ProtocolError.UnknownContribution
                    (Field.required "address" Codec.string)

            and! inProcessOnly =
                Codec.case1 "inProcessOnly" ProtocolError.InProcessOnly (Field.required "address" Codec.string)

            and! unknownMethod =
                Codec.case1 "unknownMethod" ProtocolError.UnknownMethod (Field.required "method" Codec.string)

            and! invalid =
                Codec.case1 "invalid" ProtocolError.Invalid (Field.required "issues" issues)

            and! methodFailed =
                Codec.case2
                    "methodFailed"
                    (fun message data -> ProtocolError.MethodFailed(message, data))
                    (Field.required "message" Codec.string)
                    (Field.required "data" Codec.json)

            return
                fun value ->
                    match value with
                    | ProtocolError.UnknownExtension extension -> unknownExtension extension
                    | ProtocolError.NotActive extension -> notActive extension
                    | ProtocolError.ActivationFailed(extension, reason) -> activationFailed extension reason
                    | ProtocolError.UnknownContribution address -> unknownContribution address
                    | ProtocolError.InProcessOnly address -> inProcessOnly address
                    | ProtocolError.UnknownMethod name -> unknownMethod name
                    | ProtocolError.Invalid issues -> invalid issues
                    | ProtocolError.MethodFailed(message, data) -> methodFailed message data
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
