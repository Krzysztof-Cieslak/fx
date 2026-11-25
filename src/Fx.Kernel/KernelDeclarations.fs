/// The TypeScript side of the kernel protocol: declarations of every message, generated from the
/// codecs in `KernelProtocol` (T102's generator), for kernels written in TypeScript. The reference
/// kernel (`packages/kernel-bun-ts`) imports them from `src/protocol.d.ts`, which a golden test keeps
/// equal to `render ()`.
module Fx.Kernel.KernelDeclarations

open Fx.Contracts
open Fx.Contracts.TypeScript
open Fx.Rpc

let private field (name: string) (schema: Schema) : FieldSchema =
    {
        Name = name
        Schema = schema
        Presence = Presence.Required
    }

let private named (name: string) (description: string) (fields: FieldSchema list) : Schema =
    Schema.ofKind (
        SchemaKind.Named
            {
                Name = name
                Body =
                    Schema.ofKind (
                        SchemaKind.Object
                            {
                                Fields = fields
                                UnknownFields = UnknownFields.Ignore
                            }
                    )
                    |> Schema.describe description
            }
    )

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

let private notification (n: Notification<'P>) (description: string) : FieldSchema =
    field n.Name (n.Params.Schema |> Schema.describe description)

/// The requests the host sends: method name to params, result and typed error (`error.data`).
let kernelRequests: Schema =
    named
        "KernelRequests"
        "The requests a host sends to a kernel: params, result, and the typed error carried as the data of an ApplicationError (-32000)."
        [
            request KernelProtocol.cellRun "Runs a cell. $/cancelRequest on it interrupts the cell."
            request KernelProtocol.cellCancel "Interrupts a running cell and stops what cells left running."
            request KernelProtocol.cellCheck "Optional: type-checks a cell without running it."
            request KernelProtocol.valuePreview "A bounded preview of a value the kernel holds."
            request KernelProtocol.valueEncode "A value the kernel holds, encoded."
            request KernelProtocol.valueDecode "Binds a value to a top-level variable."
            request KernelProtocol.recall "Restores the completion value of a cell (Out[cellId])."
            request KernelProtocol.snapshot "Optional (caps.snapshot): the kernel's state."
            request KernelProtocol.restore "Optional (caps.snapshot): restores a state."
        ]

/// The requests a kernel sends to its host.
let hostRequests: Schema =
    named
        "HostRequests"
        "The requests a kernel sends to its host: params, result, and the typed error carried as the data of an ApplicationError (-32000)."
        [
            request KernelProtocol.hostCall "A cell calls a binding of the host bridge."
            request KernelProtocol.finish "A cell ends the turn; the host validates the result."
        ]

/// The notifications a kernel sends to its host.
let kernelNotifications: Schema =
    named
        "KernelNotifications"
        "The notifications a kernel sends to its host."
        [
            notification KernelProtocol.cellOutput "Output of a cell, as it happens."
            notification KernelProtocol.cellActivity "Activity of a cell, from kernel-side wrappers."
        ]

/// The JSON-RPC error codes of the protocol (`Fx.Rpc.Protocol.ErrorCode`), as literal types: a kernel
/// that declares a constant of this type gets a type error if its codes differ.
let errorCodes: Schema =
    let code (name: string) (value: int) =
        field name (Schema.ofKind (SchemaKind.Const(Json.int value)))

    named
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

/// Constants of the protocol, as literal types.
let constants: Schema =
    let constant (name: string) (value: Json) =
        field name (Schema.ofKind (SchemaKind.Const value))

    named
        "KernelConstants"
        "Constants of the kernel protocol."
        [
            constant "rpcVersion" (Json.String(List.head Protocol.versions))
            constant "kernelVersion" (Json.String(List.head KernelProtocol.versions))
            constant "initializeMember" (Json.String KernelProtocol.InitializeMember)
            constant "cellIdPattern" (Json.String KernelProtocol.CellIdPattern)
        ]

/// The declaration file, as a module.
let file: DeclarationFile =
    {
        Layout = Layout.Module
        Types =
            [
                kernelRequests
                hostRequests
                kernelNotifications
                errorCodes
                constants
                Handshake.initializeParams.Schema
                Handshake.initializeResult.Schema
                KernelProtocol.Codecs.kernelSetup.Schema
                KernelProtocol.Codecs.kernelInfo.Schema
            ]
        Members = []
    }

/// The TypeScript declarations of the kernel protocol.
let render () : string = DeclarationFile.render file
