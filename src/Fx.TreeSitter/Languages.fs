namespace Fx.TreeSitter

/// The grammars linked into the library (native/tree-sitter/TreeSitter.props).
[<RequireQualifiedAccess>]
type Language =
    | FSharp
    | FSharpSignature
    | CSharp
    | Python
    | TypeScript
    | Tsx

    /// The grammar's own name (`ts_language_name`), e.g. "fsharp", "c_sharp".
    member this.Name =
        match this with
        | FSharp -> "fsharp"
        | FSharpSignature -> "fsharp_signature"
        | CSharp -> "c_sharp"
        | Python -> "python"
        | TypeScript -> "typescript"
        | Tsx -> "tsx"

    override this.ToString() = this.Name

[<RequireQualifiedAccess>]
module Language =
    let all: Language list =
        [
            Language.FSharp
            Language.FSharpSignature
            Language.CSharp
            Language.Python
            Language.TypeScript
            Language.Tsx
        ]

    /// The language of a file by its extension (as the grammars' tree-sitter.json list them), if
    /// one is linked in. Extensions compare case-insensitively.
    let ofPath (path: string) : Language option =
        let dot = path.LastIndexOf '.'
        let slash = max (path.LastIndexOf '/') (path.LastIndexOf '\\')

        if dot <= slash + 1 then
            None
        else
            match path.Substring(dot + 1).ToLowerInvariant() with
            | "fs"
            | "fsx" -> Some Language.FSharp
            | "fsi" -> Some Language.FSharpSignature
            | "cs" -> Some Language.CSharp
            | "py"
            | "pyi" -> Some Language.Python
            | "ts"
            | "mts"
            | "cts" -> Some Language.TypeScript
            | "tsx" -> Some Language.Tsx
            | _ -> None

    /// The grammar's `TSLanguage*`: static data in the library, never freed.
    let internal pointer (language: Language) : nativeint =
        match language with
        | Language.FSharp -> Native.tree_sitter_fsharp ()
        | Language.FSharpSignature -> Native.tree_sitter_fsharp_signature ()
        | Language.CSharp -> Native.tree_sitter_c_sharp ()
        | Language.Python -> Native.tree_sitter_python ()
        | Language.TypeScript -> Native.tree_sitter_typescript ()
        | Language.Tsx -> Native.tree_sitter_tsx ()

    /// The grammar's ABI version (its parser.c's LANGUAGE_VERSION).
    let abiVersion (language: Language) : uint32 =
        Native.ts_language_abi_version (pointer language)

    /// The name the grammar reports for itself; "" for grammars older than ABI 15.
    let reportedName (language: Language) : string =
        Native.text (Native.ts_language_name (pointer language))
