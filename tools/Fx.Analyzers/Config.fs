/// analyzers.json: where each rule applies (`scope`) and which files may break which rule (`allow`).
///
/// The file sits at the repository root; analyzers find it by walking up from the analyzed file.
/// File globs are relative to its directory and use `/`: `*` and `?` stay within one path segment,
/// `**` spans segments. Codes are exact (`EFF005`) or a prefix with a trailing `*` (`EFF*`).
module Fx.Analyzers.Config

open System
open System.Collections.Concurrent
open System.IO
open System.Text.Json
open System.Text.RegularExpressions

[<Literal>]
let FileName = "analyzers.json"

type Entry =
    {
        Codes: string list
        Files: string list
        Reason: string
    }

type Config =
    {
        /// A rule applies only to the files of the scope entries that name it; a rule that no
        /// entry names applies everywhere.
        Scope: Entry list
        /// Findings in these files are not reported for these codes. Every entry needs a reason.
        Allow: Entry list
    }

let empty = { Scope = []; Allow = [] }

let codeMatches (pattern: string) (code: string) =
    if pattern.EndsWith('*') then
        code.StartsWith(pattern.Substring(0, pattern.Length - 1), StringComparison.Ordinal)
    else
        String.Equals(pattern, code, StringComparison.Ordinal)

let private globToRegex (glob: string) =
    let rec go (i: int) (acc: string list) =
        if i >= glob.Length then
            acc |> List.rev |> String.concat ""
        elif String.CompareOrdinal(glob, i, "**/", 0, 3) = 0 then
            go (i + 3) ("(?:.*/)?" :: acc)
        elif String.CompareOrdinal(glob, i, "**", 0, 2) = 0 then
            go (i + 2) (".*" :: acc)
        else
            match glob[i] with
            | '*' -> go (i + 1) ("[^/]*" :: acc)
            | '?' -> go (i + 1) ("[^/]" :: acc)
            | c -> go (i + 1) (Regex.Escape(string c) :: acc)

    Regex("^" + go 0 [] + "$", RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant)

let private globs = ConcurrentDictionary<string, Regex>()

/// Whether `path` (relative to the config's directory, `/`-separated) matches `glob`.
let globMatches (glob: string) (path: string) =
    globs.GetOrAdd(glob, globToRegex).IsMatch path

let private entryMatches (code: string) (path: string) (entry: Entry) =
    List.exists (fun p -> codeMatches p code) entry.Codes
    && List.exists (fun g -> globMatches g path) entry.Files

/// Whether a finding with `code` in the file at `path` is reported.
let reports (config: Config) (code: string) (path: string) =
    let scopes =
        config.Scope
        |> List.filter (fun e -> List.exists (fun p -> codeMatches p code) e.Codes)

    let inScope = List.isEmpty scopes || List.exists (entryMatches code path) scopes
    inScope && not (List.exists (entryMatches code path) config.Allow)

let private errorsOf (result: Result<'T, string list>) =
    match result with
    | Error errors -> errors
    | Ok _ -> []

let private text (element: JsonElement) =
    match element.GetString() with
    | null -> ""
    | s -> s

let private stringArray (owner: string) (name: string) (entry: JsonElement) : Result<string list, string list> =
    match entry.TryGetProperty name with
    | true, value when value.ValueKind = JsonValueKind.Array ->
        let items = [ for item in value.EnumerateArray() -> item ]

        if List.isEmpty items then
            Error [ $"{owner}: \"{name}\" must not be empty" ]
        elif items |> List.forall (fun i -> i.ValueKind = JsonValueKind.String) then
            Ok(List.map text items)
        else
            Error [ $"{owner}: \"{name}\" must contain only strings" ]
    | true, _ -> Error [ $"{owner}: \"{name}\" must be an array of strings" ]
    | false, _ -> Error [ $"{owner}: \"{name}\" is required" ]

let private parseEntry (section: string) (index: int) (entry: JsonElement) : Result<Entry, string list> =
    let owner = $"{section}[{index}]"

    if entry.ValueKind <> JsonValueKind.Object then
        Error [ $"{owner} must be an object" ]
    else
        let unknown =
            [
                for p in entry.EnumerateObject() do
                    if not (List.contains p.Name [ "codes"; "files"; "reason" ]) then
                        $"{owner}: unknown property \"{p.Name}\""
            ]

        let reason =
            match entry.TryGetProperty "reason" with
            | true, r when r.ValueKind = JsonValueKind.String && not (String.IsNullOrWhiteSpace(text r)) -> Ok(text r)
            | true, _ -> Error [ $"{owner}: \"reason\" must be a non-empty string" ]
            | false, _ when section = "scope" -> Ok ""
            | false, _ -> Error [ $"{owner}: \"reason\" is required: say why this code may break the rule" ]

        match stringArray owner "codes" entry, stringArray owner "files" entry, reason, unknown with
        | Ok codes, Ok files, Ok reason, [] ->
            Ok
                {
                    Codes = codes
                    Files = files
                    Reason = reason
                }
        | codes, files, reason, unknown -> Error(errorsOf codes @ errorsOf files @ errorsOf reason @ unknown)

let private parseSection (root: JsonElement) (section: string) : Result<Entry list, string list> =
    match root.TryGetProperty section with
    | false, _ -> Ok []
    | true, value when value.ValueKind = JsonValueKind.Array ->
        let results = value.EnumerateArray() |> Seq.mapi (parseEntry section) |> Seq.toList

        match List.collect errorsOf results with
        | [] -> Ok(results |> List.choose Result.toOption)
        | errors -> Error errors
    | true, _ -> Error [ $"\"{section}\" must be an array" ]

/// Parses the contents of analyzers.json. `knownCodes` are the rule codes that exist; every code
/// pattern must match one of them, so a typo cannot silently allow or scope nothing.
let parse (knownCodes: string list) (json: string) : Result<Config, string list> =
    let document =
        try
            Ok(JsonDocument.Parse(json, JsonDocumentOptions(AllowTrailingCommas = true)))
        with :? JsonException as e ->
            Error [ $"invalid JSON: {e.Message}" ]

    match document with
    | Error e -> Error e
    | Ok document ->
        use document = document
        let root = document.RootElement

        if root.ValueKind <> JsonValueKind.Object then
            Error [ "the root must be an object" ]
        else
            let unknown =
                [
                    for p in root.EnumerateObject() do
                        if not (List.contains p.Name [ "scope"; "allow" ]) then
                            $"unknown property \"{p.Name}\""
                ]

            match parseSection root "scope", parseSection root "allow", unknown with
            | Ok scope, Ok allow, [] ->
                let unknownCodes =
                    [
                        for e in scope @ allow do
                            for p in e.Codes do
                                if not (List.exists (codeMatches p) knownCodes) then
                                    $"unknown rule code \"{p}\""
                    ]

                match unknownCodes with
                | [] -> Ok { Scope = scope; Allow = allow }
                | errors -> Error(List.distinct errors)
            | scope, allow, unknown -> Error(errorsOf scope @ errorsOf allow @ unknown)

/// A config together with the directory its globs are relative to.
type Located = { Root: string; Config: Config }

/// The path of `file` relative to the config's directory, with `/` separators.
let relativePath (located: Located) (file: string) =
    Path.GetRelativePath(located.Root, Path.GetFullPath file).Replace('\\', '/')

let private directoryOf (path: string) =
    match Path.GetDirectoryName path with
    | null -> path
    | dir -> dir

let rec private find (dir: string) =
    let candidate = Path.Combine(dir, FileName)

    if File.Exists candidate then
        Some candidate
    else
        match Path.GetDirectoryName dir with
        | null -> None
        | parent -> find parent

let private loaded =
    ConcurrentDictionary<string, Lazy<Result<Located, string list>>>(StringComparer.Ordinal)

/// The nearest analyzers.json at or above the directory of `file`, parsed once per run. Without one,
/// every rule applies everywhere and nothing is allowed.
let forFile (knownCodes: string list) (file: string) : Result<Located, string list> =
    let full = Path.GetFullPath file

    match find (directoryOf full) with
    | None ->
        Ok
            {
                Root = directoryOf full
                Config = empty
            }
    | Some path ->
        let load () =
            parse knownCodes (File.ReadAllText path)
            |> Result.map (fun config ->
                {
                    Root = directoryOf path
                    Config = config
                })
            |> Result.mapError (List.map (fun e -> $"{path}: {e}"))

        loaded.GetOrAdd(path, (fun _ -> Lazy<Result<Located, string list>>(Func<_> load))).Value
