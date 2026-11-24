namespace Fx.Sdk

open Fx.Contracts
open Fx.Effects

/// The error type of methods without typed errors. It has no values.
[<Sealed>]
type NoError private () =
    override _.ToString() = "no error"

    static member Codec: Codec<NoError> =
        Codec.create
            (Schema.ofKind (SchemaKind.AnyOf [])
             |> Schema.describe "This method has no typed errors.")
            (fun _ -> Json.Null)
            (fun _ -> Error [ Issue.create "this method has no typed errors" ])

/// A method of an extension's RPC contract (spec §16.1, §17 `ext/<id>/*`): its name and the codecs
/// of its params, result and typed errors. One value declares it in the manifest
/// (`ExtMethod.declare`), implements it (`ExtMethod.handle`) and decodes calls to it.
type ExtMethod<'P, 'R, 'E> =
    {
        Name: string
        Description: string option
        Params: Codec<'P>
        Result: Codec<'R>
        Error: Codec<'E>
    }

    override this.ToString() = this.Name

/// An implemented method with its types erased: JSON params in, JSON result or failure out, in the
/// extension's environment.
type MethodImpl =
    {
        Name: string
        Invoke: Json -> Eff<IExtensionEnv, Result<Json, MethodFailure>>
    }

    override this.ToString() = this.Name

[<RequireQualifiedAccess>]
module ExtMethod =
    /// A method without typed errors. Names are camelCase segments separated by `/`.
    let define (name: string) (parameters: Codec<'P>) (result: Codec<'R>) : ExtMethod<'P, 'R, NoError> =
        {
            Name = name
            Description = None
            Params = parameters
            Result = result
            Error = NoError.Codec
        }

    let describe (description: string) (m: ExtMethod<'P, 'R, 'E>) : ExtMethod<'P, 'R, 'E> =
        { m with
            Description = Some description
        }

    /// Gives a method typed errors: its expected failures.
    let withErrors (error: Codec<'E>) (m: ExtMethod<'P, 'R, _>) : ExtMethod<'P, 'R, 'E> =
        {
            Name = m.Name
            Description = m.Description
            Params = m.Params
            Result = m.Result
            Error = error
        }

    /// The method's entry in the manifest, with JSON Schemas of its params and result.
    let declare (m: ExtMethod<'P, 'R, 'E>) : MethodDecl =
        {
            Name = m.Name
            Description = m.Description
            Params = Some(Schema.toJsonSchema m.Params.Schema)
            Result = Some(Schema.toJsonSchema m.Result.Schema)
        }

    /// Implements the method. Params that do not decode never reach `handle`; its `Error` is the
    /// method's typed error, sent with its text and encoding.
    let handle (m: ExtMethod<'P, 'R, 'E>) (handle: 'P -> Eff<IExtensionEnv, Result<'R, 'E>>) : MethodImpl =
        {
            Name = m.Name
            Invoke =
                fun json ->
                    match m.Params.Decode json with
                    | Error issues -> Eff.ret (Error(MethodFailure.InvalidParams issues))
                    | Ok value ->
                        handle value
                        |> Eff.map (fun outcome ->
                            match outcome with
                            | Ok result -> Ok(m.Result.Encode result)
                            | Error e -> Error(MethodFailure.Failed(string e, m.Error.Encode e)))
        }

    /// Why a typed call failed: one of the method's typed errors, or anything else.
    [<RequireQualifiedAccess>]
    type CallError<'E> =
        | Failed of 'E
        | Other of MethodFailure

        override this.ToString() =
            match this with
            | Failed e -> string e
            | Other failure -> string failure

    /// Decodes the outcome of a call with the method's codecs.
    let decodeOutcome (m: ExtMethod<'P, 'R, 'E>) (outcome: Result<Json, MethodFailure>) : Result<'R, CallError<'E>> =
        match outcome with
        | Ok json ->
            match m.Result.Decode json with
            | Ok value -> Ok value
            | Error issues ->
                Error(CallError.Other(MethodFailure.Unreachable("invalid result:\n" + Issue.renderAtMost 10 issues)))
        | Error(MethodFailure.Failed(_, data) as failure) ->
            match m.Error.Decode data with
            | Ok typed -> Error(CallError.Failed typed)
            | Error _ -> Error(CallError.Other failure)
        | Error failure -> Error(CallError.Other failure)
