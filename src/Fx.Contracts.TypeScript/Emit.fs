namespace Fx.Contracts.TypeScript

open System
open System.Collections.Generic
open Fx.Contracts

/// A rendered type: one line, or lines that a declaration lays out.
[<NoComparison; NoEquality>]
type internal Shape =
    /// One line. `union` if it is a union, which needs parentheses inside `[]`.
    | Inline of text: string * union: bool
    /// Several lines: the first continues the line the type starts on, the others are relative to it.
    | Block of string list
    /// A union with one member per line (`| member`).
    | Alternatives of Shape list

    override this.ToString() =
        match this with
        | Inline(text, _) -> text
        | Block lines -> String.Join("\n", lines)
        | Alternatives members -> String.Join("\n", members |> List.map (fun m -> "| " + string m))

/// Laying out shapes as lines.
module internal Shapes =
    /// Longest one-line union or object type; longer ones get a line per member.
    [<Literal>]
    let Width = 80

    /// Longest one-line method signature; longer ones get a line per parameter.
    [<Literal>]
    let SignatureWidth = 100

    let atom (text: string) = Inline(text, false)

    let indent (lines: string list) =
        lines |> List.map (fun line -> if line = "" then line else "  " + line)

    let private mapFirst (f: string -> string) (lines: string list) =
        match lines with
        | first :: rest -> f first :: rest
        | [] -> [ f "" ]

    let private mapLast (f: string -> string) (lines: string list) =
        match List.rev lines with
        | last :: rest -> List.rev (f last :: rest)
        | [] -> [ f "" ]

    let rec lines (shape: Shape) : string list =
        match shape with
        | Inline(text, _) -> [ text ]
        | Block lines -> lines
        | Alternatives members ->
            members
            |> List.collect (fun m ->
                match lines m with
                | first :: rest -> ("| " + first) :: indent rest
                | [] -> [])

    /// `prefix`, the shape and `terminator`, e.g. `name: T;` or `type X = T;`. A union with a line
    /// per member starts on the next line.
    let terminated (prefix: string) (shape: Shape) (terminator: string) : string list =
        match shape with
        | Inline(text, _) -> [ prefix + text + terminator ]
        | Block block ->
            block
            |> mapFirst (fun first -> prefix + first)
            |> mapLast (fun last -> last + terminator)
        | Alternatives _ ->
            let body = lines shape |> mapLast (fun last -> last + terminator)

            if prefix = "" then
                body
            else
                prefix.TrimEnd() :: indent body

    /// The shape between `opener` and `closer`, e.g. `Promise<T>`.
    let bracketed (opener: string) (shape: Shape) (closer: string) : Shape =
        match shape with
        | Inline(text, _) -> atom (opener + text + closer)
        | Block block ->
            Block(
                block
                |> mapFirst (fun first -> opener + first)
                |> mapLast (fun last -> last + closer)
            )
        | Alternatives _ -> Block([ opener ] @ indent (lines shape) @ [ closer ])

    let arrayOf (item: Shape) : Shape =
        match item with
        | Inline(text, true) -> atom ("(" + text + ")[]")
        | Inline(text, false) -> atom (text + "[]")
        | Block block -> Block(block |> mapLast (fun last -> last + "[]"))
        | Alternatives _ -> bracketed "(" item ")[]"

    /// A union of the members, flattened, without repeated one-line members. One line if every member
    /// fits on one and the whole fits in `Width`, unless `split`.
    let union (split: bool) (members: Shape list) : Shape =
        let seen = HashSet<string>(StringComparer.Ordinal)

        let distinct =
            members
            |> List.collect (fun m ->
                match m with
                | Alternatives inner -> inner
                | other -> [ other ])
            |> List.filter (fun m ->
                match m with
                | Inline(text, _) -> seen.Add text
                | _ -> true)

        match distinct with
        | [] -> atom "never"
        | [ single ] -> single
        | many ->
            let texts =
                many
                |> List.choose (fun m ->
                    match m with
                    | Inline(text, _) -> Some text
                    | _ -> None)

            let joined = String.Join(" | ", texts)

            if not split && texts.Length = many.Length && joined.Length <= Width then
                Inline(joined, true)
            else
                Alternatives many

    /// An object type from its members: the documentation lines, the head (`name?`) and the type of
    /// each. One line if no member is documented and the whole fits in `Width`, unless `block`.
    let objectType (block: bool) (members: (string list * string * Shape) list) : Shape =
        let oneLine =
            if block then
                None
            else
                let parts =
                    members
                    |> List.map (fun (doc, head, shape) ->
                        match doc, shape with
                        | [], Inline(text, _) -> Some(head + ": " + text)
                        | _ -> None)

                if parts |> List.forall Option.isSome then
                    let text = "{ " + String.Join("; ", parts |> List.choose id) + " }"
                    if text.Length <= Width then Some text else None
                else
                    None

        match members, oneLine with
        | [], _ -> atom "{}"
        | _, Some text -> atom text
        | _, None ->
            Block(
                [
                    "{"
                    yield!
                        members
                        |> List.collect (fun (doc, head, shape) -> doc @ terminated (head + ": ") shape ";")
                        |> indent
                    "}"
                ]
            )

/// What the generator knows about the named types of a file.
[<NoComparison; NoEquality>]
type internal Definitions =
    {
        /// Bodies by schema name, sorted by name.
        Bodies: (string * Schema) list
        /// TypeScript identifiers by schema name.
        Names: IReadOnlyDictionary<string, string>
        /// Documentation by schema name: the body's description, or else the description of the root
        /// that declares it (a codec described after `Codec.named`).
        Docs: IReadOnlyDictionary<string, string>
    }

    override this.ToString() =
        "Definitions(" + String.Join(", ", this.Bodies |> List.map fst) + ")"

module internal Definitions =
    let private visitMembers (visit: Schema -> unit) (members: Member list) =
        let rec go (members: Member list) =
            for m in members |> List.sortWith (fun a b -> String.CompareOrdinal(a.Name, b.Name)) do
                match m with
                | Member.Method method ->
                    for p in method.Parameters do
                        visit p.Schema

                    visit method.Returns
                | Member.Group group -> go group.Members

        go members

    /// Every named type the roots and members use. Throws if a root is not named, two different types
    /// share a name, or a reference has no definition.
    let collect (roots: Schema list) (members: Member list) : Definitions =
        let bodies = Dictionary<string, Schema>(StringComparer.Ordinal)
        let refs = HashSet<string>(StringComparer.Ordinal)

        let rec walk (schema: Schema) =
            let fields (o: ObjectSchema) =
                for field in o.Fields do
                    walk field.Schema

            match schema.Kind with
            | SchemaKind.Named named ->
                match bodies.TryGetValue named.Name with
                | true, existing ->
                    if existing <> named.Body then
                        invalidArg (nameof roots) $"Two different types are named {Json.quote named.Name}."
                | false, _ ->
                    bodies[named.Name] <- named.Body
                    walk named.Body
            | SchemaKind.Ref name -> refs.Add name |> ignore
            | SchemaKind.Array array -> walk array.Items
            | SchemaKind.Tuple items
            | SchemaKind.AnyOf items -> List.iter walk items
            | SchemaKind.Map inner
            | SchemaKind.Nullable inner -> walk inner
            | SchemaKind.Object o -> fields o
            | SchemaKind.Union u ->
                for case in u.Cases do
                    fields case.Payload
            | SchemaKind.Any
            | SchemaKind.Null
            | SchemaKind.Boolean
            | SchemaKind.Integer _
            | SchemaKind.Number _
            | SchemaKind.String _
            | SchemaKind.Const _
            | SchemaKind.Enum _ -> ()

        let named =
            roots
            |> List.map (fun schema ->
                match schema.Kind with
                | SchemaKind.Named named -> named.Name, schema
                | _ ->
                    invalidArg
                        (nameof roots)
                        $"Types must be named schemas (Codec.named); got {Schema.render schema}. Name it to declare it.")
            |> List.sortWith (fun (a, x) (b, y) ->
                match String.CompareOrdinal(a, b) with
                | 0 -> String.CompareOrdinal(Option.toObj x.Description, Option.toObj y.Description)
                | order -> order)

        for _, root in named do
            walk root

        visitMembers walk members

        for name in refs |> Seq.sortWith (fun a b -> String.CompareOrdinal(a, b)) do
            if not (bodies.ContainsKey name) then
                invalidArg (nameof roots) $"A schema refers to {Json.quote name} but does not define it."

        let sorted =
            bodies
            |> Seq.map (fun pair -> pair.Key, pair.Value)
            |> List.ofSeq
            |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))

        // A root described after `Codec.named` documents its type, as in `Schema.toJsonSchema`.
        let docs = Dictionary<string, string>(StringComparer.Ordinal)

        for name, root in named do
            match root.Description with
            | Some d when not (docs.ContainsKey name) -> docs[name] <- d
            | _ -> ()

        for name, body in sorted do
            match body.Description with
            | Some d -> docs[name] <- d
            | None -> ()

        {
            Bodies = sorted
            Names = Names.assign Names.Kind.Type (List.map fst sorted)
            Docs = docs
        }

/// Generating TypeScript declaration files (`.d.ts`) from schemas. See docs/modules/typescript.md.
[<RequireQualifiedAccess; CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module DeclarationFile =
    open Shapes

    /// The first line of every generated file.
    [<Literal>]
    let Header =
        "// Generated from F# codecs by Fx.Contracts.TypeScript. Do not edit: change the codecs and regenerate."

    /// A file of the layout with no types or members.
    let empty (layout: Layout) : DeclarationFile =
        {
            Layout = layout
            Types = []
            Members = []
        }

    let private byName (members: Member list) =
        members |> List.sortWith (fun a b -> String.CompareOrdinal(a.Name, b.Name))

    /// A JSON value as a literal type: `"done"`, `2`, `[1, "x"]`, `{ a: true }`.
    let rec private literal (json: Json) : string =
        match json with
        | Json.Null -> "null"
        | Json.Bool true -> "true"
        | Json.Bool false -> "false"
        | Json.Number n -> (JsonNumber.normalize n).Text
        | Json.String s -> Json.quote s
        | Json.Array items -> "[" + String.Join(", ", items |> List.map literal) + "]"
        | Json.Object [] -> "{ [key: string]: never }"
        | Json.Object members ->
            "{ "
            + String.Join(
                "; ",
                members
                |> List.map (fun (key, value) -> Names.property key + ": " + literal value)
            )
            + " }"

    type private Emitter(definitions: Definitions) =

        /// Descriptions of a use of a type: of the node and, through `null`, of what it wraps. A named
        /// type's description is left out when it is the type's own documentation.
        member this.UseSite(schema: Schema) : string list =
            let own =
                match schema.Kind, schema.Description with
                | SchemaKind.Named named, Some d ->
                    match definitions.Docs.TryGetValue named.Name with
                    | true, doc when doc = d -> []
                    | _ -> [ d ]
                | _, Some d -> [ d ]
                | _, None -> []

            match schema.Kind with
            | SchemaKind.Nullable inner -> own @ this.UseSite inner |> List.distinct
            | _ -> own

        /// Descriptions, then constraints in words.
        member this.Paragraphs(schema: Schema) : string list =
            match Docs.constraints schema with
            | [] -> this.UseSite schema
            | sentences -> this.UseSite schema @ [ String.Join(" ", sentences) ]

        member this.Field(field: FieldSchema) : string list * string * Shape =
            let tags =
                match field.Presence with
                | Presence.Defaulted value -> [ Docs.defaultTag value ]
                | Presence.Required
                | Presence.Optional -> []

            let optional = if field.Presence = Presence.Required then "" else "?"

            Docs.comment (this.Paragraphs field.Schema) tags,
            Names.property field.Name + optional,
            this.Type field.Schema

        member this.Case (discriminator: string) (case: UnionCase) : Shape =
            let tag =
                Docs.comment (Option.toList case.Description) [],
                Names.property discriminator,
                atom (Json.quote case.Tag)

            objectType false (tag :: (case.Payload.Fields |> List.map this.Field))

        /// The members of a union, with nested unions and `null`s flattened, and whether to give each
        /// its own line (tagged unions always do).
        member this.Alternatives(schema: Schema) : Shape list * bool =
            match schema.Kind with
            | SchemaKind.Enum values -> values |> List.map (fun v -> atom (Json.quote v)), false
            | SchemaKind.Union u -> u.Cases |> List.map (this.Case u.Discriminator), u.Cases.Length > 1
            | SchemaKind.AnyOf schemas ->
                let parts = schemas |> List.map this.Alternatives
                List.collect fst parts, List.exists snd parts
            | SchemaKind.Nullable inner ->
                let members, split = this.Alternatives inner
                members @ [ atom "null" ], split
            | _ -> [ this.Type schema ], false

        member this.Type(schema: Schema) : Shape =
            match schema.Kind with
            | SchemaKind.Any -> atom "unknown"
            | SchemaKind.Null -> atom "null"
            | SchemaKind.Boolean -> atom "boolean"
            | SchemaKind.Integer _
            | SchemaKind.Number _ -> atom "number"
            | SchemaKind.String _ -> atom "string"
            | SchemaKind.Const value -> atom (literal value)
            | SchemaKind.Enum _
            | SchemaKind.Union _
            | SchemaKind.AnyOf _
            | SchemaKind.Nullable _ ->
                let members, split = this.Alternatives schema
                union split members
            | SchemaKind.Array array -> arrayOf (this.Type array.Items)
            | SchemaKind.Tuple items ->
                let shapes = items |> List.map this.Type

                let texts =
                    shapes
                    |> List.choose (fun s ->
                        match s with
                        | Inline(text, _) -> Some text
                        | _ -> None)

                if texts.Length = shapes.Length then
                    atom ("[" + String.Join(", ", texts) + "]")
                else
                    Block(
                        [ "[" ]
                        @ indent (shapes |> List.collect (fun s -> terminated "" s ","))
                        @ [ "]" ]
                    )
            | SchemaKind.Map values ->
                match this.Type values with
                | Inline(text, _) -> atom ("{ [key: string]: " + text + " }")
                | shape -> Block([ "{" ] @ indent (terminated "[key: string]: " shape ";") @ [ "}" ])
            | SchemaKind.Object o -> objectType false (o.Fields |> List.map this.Field)
            | SchemaKind.Ref name -> atom definitions.Names[name]
            | SchemaKind.Named named -> atom definitions.Names[named.Name]

        /// A method signature, after `prefix` (`function `, or nothing in an object type).
        member this.Method (prefix: string) (name: string) (method: Method) : string list =
            let taken = HashSet<string>(StringComparer.Ordinal)

            let names =
                method.Parameters
                |> List.map (fun p ->
                    let basis =
                        if Names.isBindable Names.Kind.Value p.Name then
                            p.Name
                        else
                            Names.sanitize Names.Kind.Value p.Name

                    let mutable candidate = basis
                    let mutable n = 2

                    while not (taken.Add candidate) do
                        candidate <- basis + string n
                        n <- n + 1

                    candidate)

            // Only trailing parameters can be optional in TypeScript.
            let lastRequired =
                method.Parameters |> List.tryFindIndexBack (fun p -> not p.Optional)

            let parameters =
                List.zip names method.Parameters
                |> List.mapi (fun i (declared, p) ->
                    let trailing = p.Optional && lastRequired |> Option.forall (fun last -> i > last)
                    let shape = this.Type p.Schema

                    let shape =
                        if p.Optional && not trailing then
                            union false [ shape; atom "undefined" ]
                        else
                            shape

                    declared + (if trailing then "?" else ""), shape)

            let returns = bracketed "Promise<" (this.Type method.Returns) ">"

            let paramTags =
                List.zip names method.Parameters
                |> List.choose (fun (declared, p) ->
                    match Option.toList p.Description @ this.Paragraphs p.Schema with
                    | [] -> None
                    | parts -> Some($"@param {declared} " + String.Join(" ", parts)))

            let returnTags =
                match this.Paragraphs method.Returns with
                | [] -> []
                | parts -> [ "@returns " + String.Join(" ", parts) ]

            let doc = Docs.comment (Option.toList method.Description) (paramTags @ returnTags)

            let inlineParameters =
                parameters
                |> List.choose (fun (head, shape) ->
                    match shape with
                    | Inline(text, _) -> Some(head + ": " + text)
                    | _ -> None)

            let oneLine =
                if inlineParameters.Length = parameters.Length then
                    let head = prefix + name + "(" + String.Join(", ", inlineParameters) + "): "

                    match terminated head returns ";" with
                    | [ line ] when line.Length > SignatureWidth && not parameters.IsEmpty -> None
                    | lines -> Some lines
                else
                    None

            match oneLine with
            | Some lines -> doc @ lines
            | None ->
                doc
                @ [ prefix + name + "(" ]
                @ indent (
                    parameters
                    |> List.collect (fun (head, shape) -> terminated (head + ": ") shape ",")
                )
                @ terminated "): " returns ";"

        member this.Group(group: Group) : Shape =
            match group.Members with
            | [] -> atom "{}"
            | members -> Block([ "{" ] @ indent (byName members |> List.collect this.Property) @ [ "}" ])

        /// A member inside an object type: a method signature or a group property.
        member this.Property(m: Member) : string list =
            match m with
            | Member.Method method -> this.Method "" (Names.property method.Name) method
            | Member.Group group ->
                Docs.comment (Option.toList group.Description) []
                @ terminated (Names.property group.Name + ": ") (this.Group group) ";"

        /// A top-level member: `function` or `const`, after `declare` (`export declare ` or nothing).
        member this.TopLevel (declare: string) (m: Member) : string list =
            match m with
            | Member.Method method -> this.Method (declare + "function ") method.Name method
            | Member.Group group ->
                Docs.comment (Option.toList group.Description) []
                @ terminated (declare + "const " + group.Name + ": ") (this.Group group) ";"

        /// The declaration of a named type, after `export` (`export ` or nothing).
        member this.Definition (export: string) (name: string) (body: Schema) : string list =
            let declared = definitions.Names[name]

            let docs =
                match definitions.Docs.TryGetValue name with
                | true, d -> [ d ]
                | false, _ -> []

            match body.Kind with
            | SchemaKind.Object o ->
                let members = o.Fields |> List.map this.Field

                Docs.comment docs []
                @ terminated (export + "interface " + declared + " ") (objectType true members) ""
            | _ ->
                let paragraphs =
                    match Docs.constraints body with
                    | [] -> docs
                    | sentences -> docs @ [ String.Join(" ", sentences) ]

                Docs.comment paragraphs []
                @ terminated (export + "type " + declared + " = ") (this.Type body) ";"

    let rec private check (topLevel: bool) (members: Member list) =
        let seen = HashSet<string>(StringComparer.Ordinal)

        for m in members do
            if not (seen.Add m.Name) then
                invalidArg (nameof members) $"Two members are named {Json.quote m.Name}."

            if topLevel && not (Names.isBindable Names.Kind.Value m.Name) then
                invalidArg
                    (nameof members)
                    $"The top-level member {Json.quote m.Name} must be an identifier that is not a reserved word."

            match m with
            | Member.Group group -> check false group.Members
            | Member.Method _ -> ()

    /// The TypeScript text of the file: the header, then the members (sorted by name), then every named
    /// type the types and members use (sorted by name). Records become interfaces, other named types
    /// type aliases; descriptions and constraints become JSDoc. The text depends only on the input,
    /// not on the order of `Types` or `Members`, and ends with a newline.
    ///
    /// Throws if a type is not named, two different types share a name, a reference has no
    /// definition, two members at one level share a name, or a top-level member or the global is not
    /// an identifier.
    let render (file: DeclarationFile) : string =
        check true file.Members

        match file.Layout with
        | Layout.Global(name, _) when not (Names.isBindable Names.Kind.Value name) ->
            invalidArg (nameof file) $"The global {Json.quote name} must be an identifier that is not a reserved word."
        | _ -> ()

        let definitions = Definitions.collect file.Types file.Members
        let emitter = Emitter definitions

        let export, declare =
            match file.Layout with
            | Layout.Module -> "export ", "export declare "
            | Layout.Global _ -> "", ""

        let declarations =
            [
                for m in byName file.Members -> emitter.TopLevel declare m
                for name, body in definitions.Bodies -> emitter.Definition export name body
            ]

        let separated (blocks: string list list) =
            blocks
            |> List.mapi (fun i block -> if i = 0 then block else "" :: block)
            |> List.concat

        let body =
            match file.Layout, declarations with
            | Layout.Module, [] -> [ "export {};" ]
            | Layout.Module, blocks -> separated blocks
            | Layout.Global(name, description), blocks ->
                Docs.comment (Option.toList description) []
                @ [ $"declare namespace {name} {{" ]
                @ indent (separated blocks)
                @ [ "}" ]

        String.Join("\n", Header :: "" :: body) + "\n"
