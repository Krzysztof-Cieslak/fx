namespace Fx.Contracts.TypeScript

open System
open Fx.Contracts

/// A parameter of a `Method`. Calls pass arguments by position, so the name only documents it (and
/// is renamed if TypeScript cannot use it).
[<NoComparison>]
type Parameter =
    {
        Name: string
        Description: string option
        Schema: Schema
        /// May be left out. An optional parameter before a required one is declared `T | undefined`.
        Optional: bool
    }

    override this.ToString() =
        let optional = if this.Optional then "?" else ""
        $"{this.Name}{optional}: {Schema.render this.Schema}"

/// A remote call, such as a host-bridge function in a kernel (`fx.shell.run`) or a method of a
/// protocol: TypeScript sees a function that returns a promise of `Returns`.
[<NoComparison>]
type Method =
    {
        /// A property name; a top-level method must be a bindable identifier (not a reserved word).
        Name: string
        Description: string option
        Parameters: Parameter list
        Returns: Schema
    }

    override this.ToString() =
        let parameters = String.Join(", ", this.Parameters |> List.map string)
        $"{this.Name}({parameters}): {Schema.render this.Returns}"

/// What a declaration file declares besides types: methods, and groups of members such as `shell` in
/// `fx.shell.run`.
[<RequireQualifiedAccess; NoComparison>]
type Member =
    | Method of Method
    | Group of Group

    member this.Name =
        match this with
        | Method m -> m.Name
        | Group g -> g.Name

    override this.ToString() =
        match this with
        | Method m -> string m
        | Group g -> string g

/// A named object of members, declared as a `const` with an object type at the top level and as a
/// property inside another group.
and [<NoComparison>] Group =
    {
        Name: string
        Description: string option
        Members: Member list
    }

    override this.ToString() =
        let names = String.Join(", ", this.Members |> List.map (fun m -> m.Name))
        $"{this.Name} {{ {names} }}"

/// The shape of a declaration file.
[<RequireQualifiedAccess>]
type Layout =
    /// An ES module (`@fx/sdk`): every declaration is exported (`export interface`, `export type`,
    /// `export declare const`, `export declare function`).
    | Module
    /// A global script (a kernel's `<global>.d.ts`): every declaration goes in
    /// `declare namespace <name>`, so that types are `<name>.Match` and members `<name>.shell.run`
    /// next to the cell's own names. `name` is the kernel global (`Fx.Product.kernelGlobal`).
    | Global of name: string * description: string option

    override this.ToString() =
        match this with
        | Module -> "module"
        | Global(name, _) -> $"global {name}"

/// The input of the generator: a layout, named types and members. Every named type the types and
/// members use is declared too, so a file needs no imports.
[<NoComparison>]
type DeclarationFile =
    {
        Layout: Layout
        /// Named schemas (`Codec.named`, `Codec.recursive`) to declare even if no member uses them.
        Types: Schema list
        Members: Member list
    }

    override this.ToString() =
        $"DeclarationFile({this.Layout}, {this.Types.Length} types, {this.Members.Length} members)"

module Parameter =
    /// A required parameter.
    let required (name: string) (schema: Schema) : Parameter =
        {
            Name = name
            Description = None
            Schema = schema
            Optional = false
        }

    /// A parameter that may be left out.
    let optional (name: string) (schema: Schema) : Parameter =
        {
            Name = name
            Description = None
            Schema = schema
            Optional = true
        }

    let describe (description: string) (parameter: Parameter) : Parameter =
        { parameter with
            Description = Some description
        }

module Method =
    /// A method without parameters or documentation.
    let create (name: string) (returns: Schema) : Method =
        {
            Name = name
            Description = None
            Parameters = []
            Returns = returns
        }

    let describe (description: string) (method: Method) : Method =
        { method with
            Description = Some description
        }

    /// Appends a parameter.
    let withParameter (parameter: Parameter) (method: Method) : Method =
        { method with
            Parameters = method.Parameters @ [ parameter ]
        }

module Member =
    /// Nests methods named by dotted paths (`shell.run`, `finish`) in groups (`shell`), as a kernel
    /// exposes its host bindings. `groups` documents groups by their path (`"shell", "Shell commands."`).
    /// Throws if a path is empty, has an empty segment, is both a method and a group, or if `groups`
    /// documents a path that is not a group.
    let ofPaths (groups: (string * string) list) (methods: Method list) : Member list =
        let documented = Collections.Generic.HashSet<string>(StringComparer.Ordinal)

        let rec build (prefix: string) (entries: (string list * Method) list) : Member list =
            entries
            |> List.groupBy (fun (segments, _) -> List.head segments)
            |> List.map (fun (segment, entries) ->
                let path = if prefix = "" then segment else prefix + "." + segment

                let leaves, nested =
                    entries |> List.partition (fun (segments, _) -> segments.Length = 1)

                match leaves, nested with
                | [ (_, method) ], [] -> Member.Method { method with Name = segment }
                | [], _ ->
                    let description = groups |> List.tryFind (fun (p, _) -> p = path) |> Option.map snd

                    if description.IsSome then
                        documented.Add path |> ignore

                    Member.Group
                        {
                            Name = segment
                            Description = description
                            Members = build path (nested |> List.map (fun (segments, m) -> List.tail segments, m))
                        }
                | _ :: _ :: _, _ -> invalidArg (nameof methods) $"Two methods are named {Json.quote path}."
                | _ -> invalidArg (nameof methods) $"{Json.quote path} is both a method and a group.")

        let entries =
            methods
            |> List.map (fun m ->
                let segments = m.Name.Split '.' |> List.ofArray

                if segments |> List.exists String.IsNullOrEmpty then
                    invalidArg (nameof methods) $"The method path {Json.quote m.Name} has an empty segment."

                segments, m)

        let members = build "" entries

        for path, _ in groups do
            if not (documented.Contains path) then
                invalidArg (nameof groups) $"{Json.quote path} is not a group of the methods."

        members
