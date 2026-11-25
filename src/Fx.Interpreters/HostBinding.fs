namespace Fx.Interpreters

open System
open Fx.Contracts
open Fx.Effects
open Fx.Kernel
open Fx.Sdk

/// An argument of a host binding: its declaration (for the bridge's declarations) and how the host
/// decodes it, into its declared type only (spec §6.6, §18): references never decode into data.
[<NoEquality; NoComparison>]
type Arg<'T> =
    {
        Param: BindingParam
        /// The argument at its position, if the cell passed one.
        Decode: Value option -> Validation<'T, Issue>
    }

    override this.ToString() = string this.Param

/// A binding of the host bridge (spec §6.5): what kernels see of it (`Decl`) and what a call does,
/// in the host's environment. Calls with arguments that do not decode never run.
[<NoEquality; NoComparison>]
type HostBinding<'R> =
    {
        Decl: BindingDecl
        Invoke: Value list -> Eff<'R, Result<Value, HostCallError>>
    }

    override this.ToString() = string this.Decl

[<RequireQualifiedAccess>]
module Arg =
    let private describe (description: string) =
        if String.IsNullOrWhiteSpace description then
            None
        else
            Some description

    let private under (name: string) (issues: Issue list) =
        issues |> List.map (Issue.under (PathSegment.Key name))

    /// A parameter the cell must pass.
    let required (name: string) (description: string) (codec: Codec<'T>) : Arg<'T> =
        {
            Param =
                {
                    Name = name
                    Description = describe description
                    Schema = codec.Schema
                    Optional = false
                }
            Decode =
                fun value ->
                    match value with
                    | Some value -> Value.decode codec value |> Result.mapError (under name)
                    | None -> Error [ Issue.at [ PathSegment.Key name ] "is required" ]
        }

    /// A parameter the cell may leave out (or pass as `null`).
    let optional (name: string) (description: string) (codec: Codec<'T>) : Arg<'T option> =
        {
            Param =
                {
                    Name = name
                    Description = describe description
                    Schema = codec.Schema
                    Optional = true
                }
            Decode =
                fun value ->
                    match value with
                    | None
                    | Some(Value.Inline Json.Null) -> Ok None
                    | Some value -> Value.decode codec value |> Result.map Some |> Result.mapError (under name)
        }

    /// An optional parameter with a default.
    let withDefault (name: string) (description: string) (fallback: 'T) (codec: Codec<'T>) : Arg<'T> =
        let arg = optional name description codec

        {
            Param = arg.Param
            Decode = fun value -> arg.Decode value |> Result.map (Option.defaultValue fallback)
        }

[<RequireQualifiedAccess>]
module HostBinding =
    let private describe (description: string) =
        if String.IsNullOrWhiteSpace description then
            None
        else
            Some description

    let private decl (path: string) (description: string) (parameters: BindingParam list) (returns: Codec<'O>) =
        {
            Path = path
            Description = describe description
            Params = parameters
            Returns = returns.Schema
        }

    let private tooMany (count: int) (args: Value list) : Issue list =
        if args.Length > count then
            [ Issue.create $"takes at most {count} arguments, got {args.Length}" ]
        else
            []

    let private answer (returns: Codec<'O>) (run: Eff<'R, Result<'O, HostCallError>>) =
        run |> EffResult.map (fun output -> Value.Inline(returns.Encode output))

    let private issuesOf (result: Validation<'T, Issue>) =
        match result with
        | Ok _ -> []
        | Error issues -> issues

    let private value (result: Validation<'T, Issue>) =
        match result with
        | Ok value -> value
        | Error _ -> invalidOp "an argument that did not decode"

    /// A binding without parameters.
    let create0
        (path: string)
        (description: string)
        (returns: Codec<'O>)
        (run: unit -> Eff<'R, Result<'O, HostCallError>>)
        : HostBinding<'R> =
        {
            Decl = decl path description [] returns
            Invoke =
                fun args ->
                    match tooMany 0 args with
                    | [] -> answer returns (run ())
                    | issues -> EffResult.error (HostCallError.InvalidArguments issues)
        }

    /// A binding with one parameter.
    let create1
        (path: string)
        (description: string)
        (a: Arg<'A>)
        (returns: Codec<'O>)
        (run: 'A -> Eff<'R, Result<'O, HostCallError>>)
        : HostBinding<'R> =
        {
            Decl = decl path description [ a.Param ] returns
            Invoke =
                fun args ->
                    let first = a.Decode(List.tryItem 0 args)

                    match tooMany 1 args @ issuesOf first with
                    | [] -> answer returns (run (value first))
                    | issues -> EffResult.error (HostCallError.InvalidArguments issues)
        }

    /// A binding with two parameters.
    let create2
        (path: string)
        (description: string)
        (a: Arg<'A>, b: Arg<'B>)
        (returns: Codec<'O>)
        (run: 'A * 'B -> Eff<'R, Result<'O, HostCallError>>)
        : HostBinding<'R> =
        {
            Decl = decl path description [ a.Param; b.Param ] returns
            Invoke =
                fun args ->
                    let first = a.Decode(List.tryItem 0 args)
                    let second = b.Decode(List.tryItem 1 args)

                    match tooMany 2 args @ issuesOf first @ issuesOf second with
                    | [] -> answer returns (run (value first, value second))
                    | issues -> EffResult.error (HostCallError.InvalidArguments issues)
        }

    /// The binding a call names, or `UnknownBinding`.
    let dispatch (bindings: HostBinding<'R> list) : HostCall -> Eff<'R, Result<Value, HostCallError>> =
        let table = bindings |> List.map (fun binding -> binding.Decl.Path, binding) |> dict

        fun call ->
            match table.TryGetValue call.Binding with
            | true, binding -> binding.Invoke call.Args
            | _ -> EffResult.error (HostCallError.UnknownBinding call.Binding)

    /// A capability's expected failure, for the cell: its kind is the error's code, its JSON the data.
    let failed (codec: Codec<'E>) (error: 'E) : HostCallError =
        let json = codec.Encode error

        let kind =
            match json with
            | Json.Object members ->
                members
                |> List.tryPick (fun (name, value) ->
                    match name, value with
                    | "kind", Json.String kind -> Some kind
                    | _ -> None)
                |> Option.defaultValue "failed"
            | _ -> "failed"

        HostCallError.Failed(kind, string error, Some json)
