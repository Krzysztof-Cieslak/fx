/// The places in a file's typed tree that the rules inspect.
module Fx.Analyzers.Sites

open System
open System.Collections.Generic
open FSharp.Analyzers.SDK
open FSharp.Compiler.Symbols
open FSharp.Compiler.Text

type Site =
    /// A call of a method, function, property accessor or constructor (e.g. a base constructor call).
    | Call of target: FSharpMemberOrFunctionOrValue * args: FSharpExpr list * range: range
    /// An object construction, `new T(...)`.
    | New of ctor: FSharpMemberOrFunctionOrValue * args: FSharpExpr list * range: range
    /// A quotation literal, `<@ ... @>`.
    | Quotation of range: range

    override s.ToString() =
        match s with
        | Call(m, _, r) -> $"Call {m.FullName} at {r}"
        | New(m, _, r) -> $"New {m.FullName} at {r}"
        | Quotation r -> $"Quotation at {r}"

let private ofExpr (e: FSharpExpr) =
    match e with
    | FSharpExprPatterns.Call(_, m, _, _, args) -> Some(Call(m, args, e.Range))
    | FSharpExprPatterns.NewObject(ctor, _, args) -> Some(New(ctor, args, e.Range))
    | FSharpExprPatterns.Quote _ -> Some(Quotation e.Range)
    | _ -> None

/// Every site in the file. Walks with an explicit stack: typed trees of long lists and sequences
/// nest deeply enough to overflow the call stack.
let ofFile (file: FSharpImplementationFileContents) : Site list =
    let pending = Stack<FSharpExpr>()

    let rec declarations (ds: FSharpImplementationFileDeclaration list) =
        for d in ds do
            match d with
            | FSharpImplementationFileDeclaration.Entity(_, nested) -> declarations nested
            | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue(_, _, body) -> pending.Push body
            | FSharpImplementationFileDeclaration.InitAction body -> pending.Push body

    declarations (TASTCollecting.getDeclarations file)

    [
        while pending.Count > 0 do
            let e = pending.Pop()

            match ofExpr e with
            | Some site -> site
            | None -> ()

            for sub in e.ImmediateSubExpressions do
                pending.Push sub
    ]

/// The full name of the type or module that declares `m`, e.g. `System.DateTime`.
let declaringType (m: FSharpMemberOrFunctionOrValue) =
    m.DeclaringEntity |> Option.bind (fun e -> e.TryFullName)

/// The member's source-level name: property accessors without `get_`/`set_`, constructors as `.ctor`.
let memberName (m: FSharpMemberOrFunctionOrValue) =
    let name = m.LogicalName

    if m.IsConstructor then
        ".ctor"
    elif
        (m.IsPropertyGetterMethod || m.IsPropertySetterMethod)
        && (name.StartsWith("get_", StringComparison.Ordinal)
            || name.StartsWith("set_", StringComparison.Ordinal))
    then
        name.Substring 4
    else
        name

let rec private stripAbbreviations (t: FSharpType) =
    if t.IsAbbreviation then
        stripAbbreviations t.AbbreviatedType
    else
        t

/// Whether `t` is one of the named types, e.g. `typeIs [ "System.String" ] t` for `string`.
let typeIs (names: string list) (t: FSharpType) =
    let t = stripAbbreviations t

    t.HasTypeDefinition
    && t.TypeDefinition.TryFullName |> Option.exists (fun n -> List.contains n names)

let private autoOpen (e: FSharpEntity) =
    e.Attributes
    |> Seq.exists (fun a -> a.AttributeType.TryFullName = Some "Microsoft.FSharp.Core.AutoOpenAttribute")

/// How a message names the API, e.g. `DateTime.Now`, `new HttpClient` or `printfn`.
let display (m: FSharpMemberOrFunctionOrValue) =
    match m.DeclaringEntity with
    | None -> m.DisplayName
    | Some e when m.IsConstructor -> $"new {e.DisplayName}"
    | Some e when e.IsFSharpModule && autoOpen e -> m.DisplayName
    | Some e -> $"{e.DisplayName}.{memberName m}"
