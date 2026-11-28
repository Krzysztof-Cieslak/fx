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

// ---- Requests from the extension process to the host (`host/*`) ---------------------------------
//
// An extension's code reaches the capabilities its manifest lists through these (T109b). Every
// request names the extension, which must be one activated on this connection; the host answers
// with that extension's environment, attenuated to its manifest.

/// The typed errors of the `host/*` requests.
[<RequireQualifiedAccess>]
type HostError =
    /// No extension of this id is active on this connection.
    | UnknownExtension of extension: string
    /// The extension's manifest does not list the capability.
    | NotGranted of extension: string * capability: string
    /// `host/contributions`: the host's catalog has no such contribution type.
    | UnknownType of contributionType: string
    /// `host/contributions`: the type's values are code, not data.
    | NotData of contributionType: string * kind: string
    /// `host/grants/*`: the grants capability's typed error.
    | Grant of error: Fx.Grants.GrantError

    override this.ToString() =
        match this with
        | UnknownExtension extension -> "no active extension " + extension + " on this connection"
        | NotGranted(extension, capability) ->
            extension
            + " has no capability "
            + capability
            + ": list it in its manifest's capabilities"
        | UnknownType name -> "unknown contribution type " + name
        | NotData(name, kind) -> name + " is not data (its values are " + kind + " code)"
        | Grant error -> string error

/// The extension's own variant slots, with their values and sources.
type HostSlots =
    {
        Slots: Fx.Variants.Resolved list
    }

    override this.ToString() = $"{this.Slots.Length} slots"

type HostPackages =
    {
        Packages: PackageInfo list
    }

    override this.ToString() = $"{this.Packages.Length} packages"

type DataParams =
    {
        Extension: string
        Type: string
    }

    override this.ToString() =
        $"{this.Extension}: data of {this.Type}"

type HostData =
    {
        Contributions: ContributedData list
    }

    override this.ToString() =
        $"{this.Contributions.Length} contributions"

type GrantCheckParams =
    {
        Extension: string
        Query: Fx.Grants.GrantQuery
    }

    override this.ToString() = $"{this.Extension}: check {this.Query}"

type GrantRequestParams =
    {
        Extension: string
        Ask: Fx.Grants.GrantAsk
    }

    override this.ToString() = $"{this.Extension}: {this.Ask}"

type GrantAwaitParams =
    {
        Extension: string
        Request: Fx.Grants.RequestId
    }

    override this.ToString() =
        $"{this.Extension}: await {this.Request}"

type GrantWithdrawParams =
    {
        Extension: string
        Request: Fx.Grants.RequestId
        Reason: string
    }

    override this.ToString() =
        $"{this.Extension}: withdraw {this.Request}"

module HostCodecs =
    module G = Fx.Grants.Codecs

    let hostError: Codec<HostError> =
        Codec.union "kind" {
            let! unknownExtension =
                Codec.case1 "unknownExtension" HostError.UnknownExtension (Field.required "extension" Codec.string)

            and! notGranted =
                Codec.case2
                    "notGranted"
                    (fun extension capability -> HostError.NotGranted(extension, capability))
                    (Field.required "extension" Codec.string)
                    (Field.required "capability" Codec.string)

            and! unknownType =
                Codec.case1 "unknownType" HostError.UnknownType (Field.required "type" Codec.string)

            and! notData =
                Codec.case2
                    "notData"
                    (fun name kind -> HostError.NotData(name, kind))
                    (Field.required "type" Codec.string)
                    (Field.required "contributionKind" Codec.string)

            and! grant = Codec.case1 "grant" HostError.Grant (Field.required "error" G.error)

            return
                fun value ->
                    match value with
                    | HostError.UnknownExtension extension -> unknownExtension extension
                    | HostError.NotGranted(extension, capability) -> notGranted extension capability
                    | HostError.UnknownType name -> unknownType name
                    | HostError.NotData(name, kind) -> notData name kind
                    | HostError.Grant error -> grant error
        }
        |> Codec.named "HostError"

    let hostSlots: Codec<HostSlots> =
        Codec.record {
            let! slots =
                Codec.field "slots" (fun (s: HostSlots) -> s.Slots) (Codec.list Fx.Variants.Resolved.codec)

            return { Slots = slots }
        }
        |> Codec.named "HostSlots"

    let packageContribution: Codec<PackageContribution> =
        Codec.record {
            let! kind = Codec.field "type" (fun (c: PackageContribution) -> c.Type) Codec.string
            and! id = Codec.field "id" (fun c -> c.Id) Codec.string
            return { Type = kind; Id = id }
        }
        |> Codec.named "PackageContribution"

    let packageInfo: Codec<PackageInfo> =
        Codec.record {
            let! id = Codec.field "id" (fun (p: PackageInfo) -> p.Id) Codec.string
            and! version = Codec.field "version" (fun p -> p.Version) Codec.string

            and! description =
                Codec.optionalField "description" (fun p -> p.Description) Codec.string

            and! runtime = Codec.field "runtime" (fun p -> p.Runtime) Codec.string
            and! source = Codec.field "source" (fun p -> p.Source) Codec.string

            and! contributions =
                Codec.field "contributions" (fun p -> p.Contributions) (Codec.list packageContribution)

            and! methods = Codec.field "methods" (fun p -> p.Methods) (Codec.list Codec.string)

            return
                {
                    Id = id
                    Version = version
                    Description = description
                    Runtime = runtime
                    Source = source
                    Contributions = contributions
                    Methods = methods
                }
        }
        |> Codec.describe "An extension in effect, as the packages capability describes it."
        |> Codec.named "PackageInfo"

    let hostPackages: Codec<HostPackages> =
        Codec.record {
            let! packages =
                Codec.field "packages" (fun (p: HostPackages) -> p.Packages) (Codec.list packageInfo)

            return { Packages = packages }
        }
        |> Codec.named "HostPackages"

    let dataParams: Codec<DataParams> =
        Codec.record {
            let! extension =
                Codec.field "extension" (fun (p: DataParams) -> p.Extension) Codec.string

            and! kind = Codec.field "type" (fun p -> p.Type) Codec.string
            return { Extension = extension; Type = kind }
        }
        |> Codec.named "DataParams"

    let contributedData: Codec<ContributedData> =
        Codec.record {
            let! extension =
                Codec.field "extension" (fun (c: ContributedData) -> c.Extension) Codec.string

            and! id = Codec.field "id" (fun c -> c.Id) Codec.string
            and! value = Codec.field "value" (fun c -> c.Value) Codec.json

            return
                {
                    Extension = extension
                    Id = id
                    Value = value
                }
        }
        |> Codec.named "ContributedData"

    let hostData: Codec<HostData> =
        Codec.record {
            let! contributions =
                Codec.field "contributions" (fun (d: HostData) -> d.Contributions) (Codec.list contributedData)

            return { Contributions = contributions }
        }
        |> Codec.named "HostData"

    let grantCheckParams: Codec<GrantCheckParams> =
        Codec.record {
            let! extension =
                Codec.field "extension" (fun (p: GrantCheckParams) -> p.Extension) Codec.string

            and! query = Codec.field "query" (fun p -> p.Query) G.query
            return { Extension = extension; Query = query }
        }
        |> Codec.named "GrantCheckParams"

    let grantRequestParams: Codec<GrantRequestParams> =
        Codec.record {
            let! extension =
                Codec.field "extension" (fun (p: GrantRequestParams) -> p.Extension) Codec.string

            and! ask = Codec.field "ask" (fun p -> p.Ask) G.ask
            return { Extension = extension; Ask = ask }
        }
        |> Codec.named "GrantRequestParams"

    let grantAwaitParams: Codec<GrantAwaitParams> =
        Codec.record {
            let! extension =
                Codec.field "extension" (fun (p: GrantAwaitParams) -> p.Extension) Codec.string

            and! request = Codec.field "request" (fun p -> p.Request) G.requestId

            return
                {
                    Extension = extension
                    Request = request
                }
        }
        |> Codec.named "GrantAwaitParams"

    let grantWithdrawParams: Codec<GrantWithdrawParams> =
        Codec.record {
            let! extension =
                Codec.field "extension" (fun (p: GrantWithdrawParams) -> p.Extension) Codec.string

            and! request = Codec.field "request" (fun p -> p.Request) G.requestId
            and! reason = Codec.field "reason" (fun p -> p.Reason) Codec.string

            return
                {
                    Extension = extension
                    Request = request
                    Reason = reason
                }
        }
        |> Codec.named "GrantWithdrawParams"

/// The values of the extension's own variant slots (`variants`). An extension process asks once,
/// when the extension activates: the host's values do not change while it runs.
let hostVariants =
    Method.define "host/variants" Codecs.extensionRef HostCodecs.hostSlots
    |> Method.withErrors HostCodecs.hostError

/// The extensions in effect (`packages`).
let hostPackages =
    Method.define "host/packages" Codecs.extensionRef HostCodecs.hostPackages
    |> Method.withErrors HostCodecs.hostError

/// The data contributions of a type in effect (`packages`).
let hostContributions =
    Method.define "host/contributions" HostCodecs.dataParams HostCodecs.hostData
    |> Method.withErrors HostCodecs.hostError

/// What the effective rules say about a query (`grants`).
let hostGrantsCheck =
    Method.define "host/grants/check" HostCodecs.grantCheckParams Fx.Grants.Codecs.check
    |> Method.withErrors HostCodecs.hostError

/// Asks for a permission as the extension (`grants`).
let hostGrantsRequest =
    Method.define "host/grants/request" HostCodecs.grantRequestParams Fx.Grants.Codecs.askOutcome
    |> Method.withErrors HostCodecs.hostError

/// Waits for a request the extension made (`grants`).
let hostGrantsAwait =
    Method.define "host/grants/await" HostCodecs.grantAwaitParams Fx.Grants.Codecs.resolution
    |> Method.withErrors HostCodecs.hostError

/// Withdraws a request the extension made (`grants`).
let hostGrantsWithdraw =
    Method.define "host/grants/withdraw" HostCodecs.grantWithdrawParams Codec.unit
    |> Method.withErrors HostCodecs.hostError
