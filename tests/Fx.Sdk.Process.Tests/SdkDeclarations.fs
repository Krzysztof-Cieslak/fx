/// The generated files of the TypeScript SDK (`packages/sdk/src/generated`): the types of the
/// contracts an extension sees, rendered from the F# codecs with T102's generator, and the product's
/// constants. The SDK imports them; the tests keep the checked-in files equal to these.
module Fx.Sdk.Process.Tests.SdkDeclarations

open Fx
open Fx.Contracts
open Fx.Contracts.TypeScript
open Fx.Rpc
open Fx.Variants
open Fx.Sdk
open Fx.Sdk.Process
open Fx.Models

let private field (name: string) (schema: Schema) : FieldSchema =
    {
        Name = name
        Schema = schema
        Presence = Presence.Required
    }

let private named (name: string) (description: string) (schema: Schema) : Schema =
    Schema.ofKind (
        SchemaKind.Named
            {
                Name = name
                Body = schema |> Schema.describe description
            }
    )

let private record (name: string) (description: string) (fields: FieldSchema list) : Schema =
    named
        name
        description
        (Schema.ofKind (
            SchemaKind.Object
                {
                    Fields = fields
                    UnknownFields = UnknownFields.Ignore
                }
        ))

let private request (m: Method<'P, 'R, 'E>) (description: string) : FieldSchema =
    field
        m.Name
        (Schema.ofKind (
            SchemaKind.Object
                {
                    Fields =
                        [
                            field "params" m.Params.Schema
                            field "result" m.Result.Schema
                            field "error" m.Error.Schema
                        ]
                    UnknownFields = UnknownFields.Ignore
                }
         )
         |> Schema.describe description)

/// The requests a host sends to an extension process.
let extensionRequests: Schema =
    record
        "ExtensionRequests"
        "The requests a host sends to an extension process: params, result, and the typed error carried as the data of an ApplicationError (-32000)."
        [
            request
                ExtensionProtocol.activate
                "Activates an extension the process serves, or loads a package with a runtime it contributes."
            request ExtensionProtocol.invoke "Runs a contribution's code: a gate's check, a transform, an observer."
            request ExtensionProtocol.call "Calls a method of an extension's RPC contract."
            request ExtensionProtocol.deactivate "Deactivates an extension."
        ]

/// The requests an extension process sends to the host (`host/*`, T109b).
let hostRequests: Schema =
    record
        "HostRequests"
        "The requests an extension process sends to the host for the capabilities its extensions' manifests list: params, result, and the typed error carried as the data of an ApplicationError (-32000)."
        [
            request
                ExtensionProtocol.hostVariants
                "The values of the extension's own variant slots (variants), asked once when it activates."
            request ExtensionProtocol.hostPackages "The extensions in effect (packages)."
            request ExtensionProtocol.hostContributions "The data contributions of a type in effect (packages)."
            request ExtensionProtocol.hostGrantsCheck "What the effective rules say about a query (grants)."
            request ExtensionProtocol.hostGrantsRequest "Asks for a permission as the extension (grants)."
            request ExtensionProtocol.hostGrantsAwait "Waits for a request the extension made (grants)."
            request ExtensionProtocol.hostGrantsWithdraw "Withdraws a request the extension made (grants)."
        ]

/// The JSON-RPC error codes (`Fx.Rpc.Protocol.ErrorCode`), as literal types.
let errorCodes: Schema =
    let code (name: string) (value: int) =
        field name (Schema.ofKind (SchemaKind.Const(Json.int value)))

    record
        "ErrorCodes"
        "The JSON-RPC error codes (docs/modules/rpc.md)."
        [
            code "parseError" Protocol.ErrorCode.ParseError
            code "invalidRequest" Protocol.ErrorCode.InvalidRequest
            code "methodNotFound" Protocol.ErrorCode.MethodNotFound
            code "invalidParams" Protocol.ErrorCode.InvalidParams
            code "internalError" Protocol.ErrorCode.InternalError
            code "applicationError" Protocol.ErrorCode.ApplicationError
            code "requestCancelled" Protocol.ErrorCode.RequestCancelled
            code "notInitialized" Protocol.ErrorCode.NotInitialized
            code "overloaded" Protocol.ErrorCode.Overloaded
            code "unsupportedVersion" Protocol.ErrorCode.UnsupportedVersion
            code "responseTooLarge" Protocol.ErrorCode.ResponseTooLarge
            code "shuttingDown" Protocol.ErrorCode.ShuttingDown
        ]

/// Constants of the extension protocol and the SDK, as literal types.
let constants: Schema =
    let constant (name: string) (value: string) =
        field name (Schema.ofKind (SchemaKind.Const(Json.String value)))

    record
        "ExtensionConstants"
        "Constants of the extension protocol and the SDK."
        [
            constant "rpcVersion" (List.head Protocol.versions)
            constant "protocolVersion" Versions.Protocol
            constant "sdkVersion" Versions.Sdk
            constant "versionMember" ExtensionProtocol.VersionMember
            constant "servesMember" ExtensionProtocol.ServesMember
        ]

/// The declarations of `@fx/sdk`'s contracts: schemas, issues, manifests, decisions, variant slots,
/// every message of the extension protocol, and the canonical model.
let contracts: DeclarationFile =
    {
        Layout = Layout.Module
        Types =
            [
                Codecs.schema.Schema
                Codecs.issue.Schema
                Manifest.codec.Schema
                Decision.codec.Schema
                named "TransformSettings" "A transform's settings: its priority." Transform.settings.Schema
                SlotInfo.declCodec.Schema
                ExtensionProtocol.Codecs.activateParams.Schema
                ExtensionProtocol.Codecs.activated.Schema
                ExtensionProtocol.Codecs.invokeParams.Schema
                ExtensionProtocol.Codecs.callParams.Schema
                ExtensionProtocol.Codecs.extensionRef.Schema
                named
                    "Output"
                    "What extension/invoke and extension/call answer: the output, wrapped so that null and scalars travel as results too."
                    ExtensionProtocol.Codecs.output.Schema
                ExtensionProtocol.Codecs.protocolError.Schema
                extensionRequests
                ExtensionProtocol.HostCodecs.hostError.Schema
                ExtensionProtocol.HostCodecs.packageInfo.Schema
                ExtensionProtocol.HostCodecs.contributedData.Schema
                ExtensionProtocol.HostCodecs.dataParams.Schema
                ExtensionProtocol.HostCodecs.hostSlots.Schema
                ExtensionProtocol.HostCodecs.hostPackages.Schema
                ExtensionProtocol.HostCodecs.hostData.Schema
                ExtensionProtocol.HostCodecs.grantCheckParams.Schema
                ExtensionProtocol.HostCodecs.grantRequestParams.Schema
                ExtensionProtocol.HostCodecs.grantAwaitParams.Schema
                ExtensionProtocol.HostCodecs.grantWithdrawParams.Schema
                Fx.Grants.Codecs.check.Schema
                Fx.Grants.Codecs.askOutcome.Schema
                Fx.Grants.Codecs.resolution.Schema
                Fx.Grants.Codecs.error.Schema
                hostRequests
                errorCodes
                constants
                Handshake.initializeParams.Schema
                Handshake.initializeResult.Schema
                // The canonical model (docs/modules/models.md): requests, streamed events, responses,
                // typed errors, model metadata and provider definitions.
                ChatRequest.codec.Schema
                ChatEvent.codec.Schema
                ChatResponse.codec.Schema
                ChatFailure.codec.Schema
                ModelInfo.codec.Schema
                ProviderDef.codec.Schema
            ]
        Members = []
    }

/// `packages/sdk/src/generated/contracts.d.ts`.
let renderContracts () : string = DeclarationFile.render contracts

/// `packages/sdk/src/generated/product.ts`: the names TypeScript code derives from the product name,
/// so that nothing in the SDK hardcodes it.
let renderProduct () : string =
    let quote = Json.quote
    let sampleId = Product.extensionId "sample"
    let prefix = Product.extensionId ""

    String.concat
        "\n"
        [
            "// Generated from Fx.Product, Fx.Sdk.Versions and Fx.Sdk.InjectionVectors by tests/Fx.Sdk.Process.Tests."
            "// Do not edit: change the constants and run the tests with UPDATE_GOLDEN=1."
            ""
            "/** The product name: the build-time constant every user-visible name derives from. */"
            $"export const productName = {quote Product.Name};"
            ""
            "/** The product's version. */"
            $"export const productVersion = {quote Product.Version};"
            ""
            "/** The extension API version this SDK implements (`Versions.Sdk`): a manifest's `sdk`. */"
            $"export const sdkVersion = {quote Versions.Sdk};"
            ""
            "/** The extension protocol version this SDK speaks (`Versions.Protocol`): a manifest's `protocol`. */"
            $"export const protocolVersion = {quote Versions.Protocol};"
            ""
            "/** The file name of a manifest in a package directory. */"
            $"export const manifestFileName = {quote Manifest.fileName};"
            ""
            $"/** A bundled extension's id: `extensionId(\"sample\")` is `{sampleId}`. */"
            "export function extensionId(localId: string): string {"
            $"  return {quote prefix} + localId;"
            "}"
            ""
            "/**"
            " * The runtime injection vectors (spec §18): variable names, or prefixes ending in `*`, that make a"
            " * runtime load code before the program does. The host removes them from every extension process;"
            " * a manifest may not set them."
            " */"
            "export const injectionVectors: readonly string[] = ["
            yield! InjectionVectors.patterns |> List.map (fun pattern -> $"  {quote pattern},")
            "];"
            ""
        ]
