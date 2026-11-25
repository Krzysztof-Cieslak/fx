/// TypeScript identifiers: which names can be declared, quoting property names, and giving every
/// declared name a distinct identifier.
module internal Fx.Contracts.TypeScript.Names

open System
open System.Collections.Generic
open Fx.Contracts

/// Words that cannot name a value (a parameter, a top-level `const` or `function`, the global):
/// JavaScript's reserved words, also those of strict mode and modules, which declaration files are.
let private reservedWords =
    [
        "arguments"
        "await"
        "break"
        "case"
        "catch"
        "class"
        "const"
        "continue"
        "debugger"
        "default"
        "delete"
        "do"
        "else"
        "enum"
        "eval"
        "export"
        "extends"
        "false"
        "finally"
        "for"
        "function"
        "if"
        "implements"
        "import"
        "in"
        "instanceof"
        "interface"
        "let"
        "new"
        "null"
        "package"
        "private"
        "protected"
        "public"
        "return"
        "static"
        "super"
        "switch"
        "this"
        "throw"
        "true"
        "try"
        "typeof"
        "undefined"
        "var"
        "void"
        "while"
        "with"
        "yield"
    ]

/// What else cannot name a type, or would change what the generated code means: TypeScript's
/// predefined types and type-level keywords, and the globals the generated code refers to.
let private typeWords =
    [
        "abstract"
        "accessor"
        "any"
        "as"
        "asserts"
        "async"
        "bigint"
        "boolean"
        "constructor"
        "declare"
        "from"
        "get"
        "global"
        "infer"
        "intrinsic"
        "is"
        "keyof"
        "module"
        "namespace"
        "never"
        "number"
        "object"
        "of"
        "out"
        "override"
        "readonly"
        "require"
        "satisfies"
        "set"
        "string"
        "symbol"
        "type"
        "unique"
        "unknown"
        "using"
        "Promise"
        "globalThis"
    ]

/// What a name declares: a type, or a value (parameters, top-level members, the global).
[<RequireQualifiedAccess>]
type Kind =
    | Type
    | Value

    override this.ToString() =
        match this with
        | Type -> "type"
        | Value -> "value"

let private reservedValues = HashSet<string>(reservedWords, StringComparer.Ordinal)

let private reservedTypes =
    HashSet<string>(reservedWords @ typeWords, StringComparer.Ordinal)

let private reserved (kind: Kind) =
    match kind with
    | Kind.Type -> reservedTypes
    | Kind.Value -> reservedValues

let private isStart (c: char) =
    (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c = '_' || c = '$'

let private isPart (c: char) = isStart c || (c >= '0' && c <= '9')

/// An ASCII identifier (`[A-Za-z_$][A-Za-z0-9_$]*`). Other identifiers TypeScript accepts are quoted
/// or renamed, so that the output does not depend on Unicode tables.
let isIdentifier (name: string) =
    name.Length > 0 && isStart name[0] && Seq.forall isPart name

/// Whether `name` can be declared as is: an identifier that is not reserved for its kind.
let isBindable (kind: Kind) (name: string) =
    isIdentifier name && not ((reserved kind).Contains name)

/// A bindable identifier close to `name`: other characters become `_`, a leading digit gets a `_`
/// before it, and a reserved word gets a `_` after it.
let sanitize (kind: Kind) (name: string) : string =
    let replaced =
        String(name.ToCharArray() |> Array.map (fun c -> if isPart c then c else '_'))

    let started =
        if replaced.Length = 0 || not (isStart replaced[0]) then
            "_" + replaced
        else
            replaced

    if (reserved kind).Contains started then
        started + "_"
    else
        started

/// A property name: the name itself if it is an identifier (reserved words are valid property
/// names), otherwise a string literal.
let property (name: string) : string =
    if isIdentifier name then name else Json.quote name

/// Gives each name a distinct bindable identifier. Bindable names keep themselves; the others are
/// sanitized, in the given order, with a number appended (`Promise_2`, `my_type2`) if the result is
/// taken.
/// The result depends only on the list, so the same names always get the same identifiers.
let assign (kind: Kind) (names: string list) : IReadOnlyDictionary<string, string> =
    let assigned = Dictionary<string, string>(StringComparer.Ordinal)
    let taken = HashSet<string>(StringComparer.Ordinal)

    for name in names do
        if not (assigned.ContainsKey name) && isBindable kind name then
            assigned[name] <- name
            taken.Add name |> ignore

    for name in names do
        if not (assigned.ContainsKey name) then
            let basis = sanitize kind name
            let mutable candidate = basis
            let mutable n = 2

            while taken.Contains candidate do
                candidate <- basis + string n
                n <- n + 1

            assigned[name] <- candidate
            taken.Add candidate |> ignore

    assigned
