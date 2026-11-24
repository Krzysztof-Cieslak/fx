/// AOT003 and EFF001–EFF009: banned APIs.
///
/// A use of an API is a call, property access or construction whose declaring type matches a ban.
/// The first matching ban decides the code, so specific bans come before broad ones. Extend the
/// tables when a new way to do reflection or I/O shows up; keep every entry a precise API name.
module Fx.Analyzers.Apis

open System
open FSharp.Compiler.Symbols
open Fx.Analyzers.Sites

type Target =
    /// Every type in the namespace and its sub-namespaces.
    | Namespace of string
    /// Every member and constructor of the type.
    | Type of string
    /// The named members of the type (property names without `get_`).
    | Members of typeName: string * names: string list
    /// The overloads of a member with exactly this many parameters.
    | Overload of typeName: string * name: string * parameters: int
    /// Constructors whose first parameter is a file path string (e.g. `new StreamReader(path)`).
    | PathConstructor of typeName: string
    /// Functions implemented in native code: `extern` declarations with `[<DllImport>]`.
    | PInvoke

    override t.ToString() =
        match t with
        | Namespace ns -> $"namespace {ns}"
        | Type name -> $"type {name}"
        | Members(name, members) ->
            let joined = String.concat ", " members
            $"{name}.[{joined}]"
        | Overload(name, m, n) -> $"{name}.{m}/{n}"
        | PathConstructor name -> $"new {name}(path)"
        | PInvoke -> "P/Invoke"

type Ban =
    {
        Rule: Rules.Rule
        Target: Target
    }

    override b.ToString() = $"{b.Rule.Code} {b.Target}"

let private parameters (m: FSharpMemberOrFunctionOrValue) =
    m.CurriedParameterGroups |> Seq.concat |> Seq.toList

let private matches (target: Target) (typeName: string) (m: FSharpMemberOrFunctionOrValue) =
    match target with
    | Namespace ns -> typeName.StartsWith(ns + ".", StringComparison.Ordinal)
    | Type name -> typeName = name
    | Members(name, members) -> typeName = name && List.contains (memberName m) members
    | Overload(name, member', count) -> typeName = name && memberName m = member' && List.length (parameters m) = count
    | PathConstructor name ->
        typeName = name
        && m.IsConstructor
        && (match parameters m with
            | first :: _ -> typeIs [ "System.String" ] first.Type
            | [] -> false)
    | PInvoke ->
        m.Attributes
        |> Seq.exists (fun a -> a.AttributeType.TryFullName = Some "System.Runtime.InteropServices.DllImportAttribute")

/// APIs that look banned by a broad target but are fine.
let allowed =
    [
        // `typeof<T>.Name` and `x.GetType().Name`: the name needs no reflection metadata.
        Members("System.Reflection.MemberInfo", [ "Name" ])
        Members("System.Environment", [ "NewLine" ])
        // Reading metadata from files (e.g. for architecture tests) is not runtime reflection.
        Namespace "System.Reflection.Metadata"
        Namespace "System.Reflection.PortableExecutable"
    ]

let private ban rule target = { Rule = rule; Target = target }

let bans: Ban list =
    [
        // EFF008: native code. First, so a P/Invoke is reported as such whatever declares it.
        ban Rules.nativeCode PInvoke

        // AOT003: runtime reflection, dynamic code and reflection-based serializers.
        for ns in
            [
                "System.Reflection"
                "System.Runtime.Loader"
                "System.Linq.Expressions"
                "System.Dynamic"
                "Microsoft.CSharp.RuntimeBinder"
                "Microsoft.FSharp.Reflection"
                "Microsoft.FSharp.Quotations"
                "Microsoft.FSharp.Linq.RuntimeHelpers"
                "System.Text.Json.Serialization"
                "System.Net.Http.Json"
                "System.Runtime.Serialization"
                "System.Xml.Serialization"
                "Newtonsoft.Json"
            ] do
            ban Rules.reflection (Namespace ns)
        ban Rules.reflection (Type "System.Activator")
        ban Rules.reflection (Type "System.Text.Json.JsonSerializer")
        ban Rules.reflection (Type "System.ComponentModel.TypeDescriptor")
        ban
            Rules.reflection
            (Members(
                "System.Type",
                [
                    "GetType"
                    "GetTypeFromProgID"
                    "GetTypeFromCLSID"
                    "Assembly"
                    "Module"
                    "TypeInitializer"
                    "GetMethod"
                    "GetMethods"
                    "GetProperty"
                    "GetProperties"
                    "GetField"
                    "GetFields"
                    "GetMember"
                    "GetMembers"
                    "GetConstructor"
                    "GetConstructors"
                    "GetEvent"
                    "GetEvents"
                    "GetNestedType"
                    "GetNestedTypes"
                    "GetInterface"
                    "GetInterfaces"
                    "GetDefaultMembers"
                    "FindMembers"
                    "FindInterfaces"
                    "InvokeMember"
                    "MakeGenericType"
                    "MakeArrayType"
                    "MakeByRefType"
                    "MakePointerType"
                    "GetCustomAttributes"
                    "GetCustomAttributesData"
                    "IsDefined"
                ]
            ))
        ban Rules.reflection (Members("System.Attribute", [ "GetCustomAttribute"; "GetCustomAttributes"; "IsDefined" ]))
        ban Rules.reflection (Members("System.Delegate", [ "DynamicInvoke"; "CreateDelegate"; "Method" ]))
        ban
            Rules.reflection
            (Members(
                "System.AppDomain",
                [
                    "GetAssemblies"
                    "Load"
                    "CreateInstance"
                    "CreateInstanceAndUnwrap"
                    "CreateInstanceFrom"
                    "CreateInstanceFromAndUnwrap"
                    "ExecuteAssembly"
                    "ExecuteAssemblyByName"
                    "ReflectionOnlyGetAssemblies"
                    "add_AssemblyResolve"
                    "add_TypeResolve"
                    "add_ResourceResolve"
                    "add_AssemblyLoad"
                ]
            ))
        // The non-generic overloads need runtime type information; `Enum.GetValues<T>()` is fine.
        ban Rules.reflection (Overload("System.Enum", "GetValues", 1))
        ban Rules.reflection (Overload("System.Enum", "GetNames", 1))

        // EFF001: clock and timers.
        ban Rules.clock (Members("System.DateTime", [ "Now"; "UtcNow"; "Today" ]))
        ban Rules.clock (Members("System.DateTimeOffset", [ "Now"; "UtcNow" ]))
        ban Rules.clock (Members("System.Environment", [ "TickCount"; "TickCount64" ]))
        ban Rules.clock (Members("System.TimeProvider", [ "System" ]))
        ban Rules.clock (Members("System.Threading.Thread", [ "Sleep" ]))
        ban Rules.clock (Members("System.Threading.Tasks.Task", [ "Delay" ]))
        ban Rules.clock (Members("Microsoft.FSharp.Control.FSharpAsync", [ "Sleep" ]))

        for t in
            [
                "System.Diagnostics.Stopwatch"
                "System.Threading.Timer"
                "System.Threading.PeriodicTimer"
                "System.Timers.Timer"
            ] do
            ban Rules.clock (Type t)

        // EFF002: randomness and ids.
        ban Rules.randomness (Type "System.Random")
        ban Rules.randomness (Type "System.Security.Cryptography.RandomNumberGenerator")
        ban Rules.randomness (Members("System.Guid", [ "NewGuid"; "CreateVersion7" ]))
        ban Rules.randomness (Members("System.IO.Path", [ "GetRandomFileName" ]))

        // EFF003: the process environment.
        ban Rules.environment (Type "System.Environment")
        ban Rules.environment (Type "System.AppContext")
        ban Rules.environment (Type "Microsoft.Win32.Registry")
        ban Rules.environment (Type "Microsoft.Win32.RegistryKey")
        ban
            Rules.environment
            (Members(
                "System.TimeZoneInfo",
                [
                    "Local"
                    "FindSystemTimeZoneById"
                    "TryFindSystemTimeZoneById"
                    "GetSystemTimeZones"
                    "ClearCachedData"
                ]
            ))

        // EFF004: the filesystem.
        for t in
            [
                "System.IO.File"
                "System.IO.Directory"
                "System.IO.FileInfo"
                "System.IO.DirectoryInfo"
                "System.IO.FileSystemInfo"
                "System.IO.DriveInfo"
                "System.IO.FileStream"
                "System.IO.FileSystemWatcher"
                "System.IO.RandomAccess"
                "System.IO.Compression.ZipFile"
                "System.IO.Compression.ZipFileExtensions"
            ] do
            ban Rules.filesystem (Type t)
        for ns in
            [
                "System.IO.Enumeration"
                "System.IO.MemoryMappedFiles"
                "System.IO.IsolatedStorage"
            ] do
            ban Rules.filesystem (Namespace ns)
        ban Rules.filesystem (PathConstructor "System.IO.StreamReader")
        ban Rules.filesystem (PathConstructor "System.IO.StreamWriter")
        ban Rules.filesystem (Members("System.IO.Path", [ "GetTempPath"; "GetTempFileName"; "Exists" ]))
        // `Path.GetFullPath(path)` resolves against the current directory; the two-argument overload is pure.
        ban Rules.filesystem (Overload("System.IO.Path", "GetFullPath", 1))

        // EFF005: the console.
        ban Rules.console (Type "System.Console")
        ban Rules.console (Members("Microsoft.FSharp.Core.Operators", [ "stdin"; "stdout"; "stderr" ]))
        for printing in
            [
                "Microsoft.FSharp.Core.ExtraTopLevelOperators"
                "Microsoft.FSharp.Core.PrintfModule"
            ] do
            ban Rules.console (Members(printing, [ "printf"; "printfn"; "eprintf"; "eprintfn" ]))

        // EFF006: processes, including .NET 11's process handles and custom process creation.
        for t in
            [
                "System.Diagnostics.Process"
                "Microsoft.Win32.SafeHandles.SafeProcessHandle"
                "System.Runtime.InteropServices.UnixProcessStartArguments"
                "System.Runtime.InteropServices.WindowsProcessStartArguments"
            ] do
            ban Rules.processes (Type t)

        // EFF007: the network and inter-process communication.
        for ns in
            [
                "System.Net.Sockets"
                "System.Net.NetworkInformation"
                "System.Net.Quic"
                "System.IO.Pipes"
            ] do
            ban Rules.network (Namespace ns)
        for t in
            [
                "System.Net.Http.HttpClient"
                "System.Net.Http.HttpMessageInvoker"
                "System.Net.Http.HttpClientHandler"
                "System.Net.Http.SocketsHttpHandler"
                "System.Net.WebSockets.ClientWebSocket"
                "System.Net.Dns"
                "System.Net.WebClient"
                "System.Net.WebRequest"
                "System.Net.HttpWebRequest"
                "System.Net.FtpWebRequest"
                "System.Net.HttpListener"
                "System.Net.Mail.SmtpClient"
            ] do
            ban Rules.network (Type t)

        // EFF009: storage. The SQLite binding's file I/O happens in native code, which the
        // filesystem rule cannot see.
        ban Rules.storage (Namespace "Fx.Sqlite")
    ]

/// The ban a use of `m` violates, if any.
let banned (m: FSharpMemberOrFunctionOrValue) =
    match declaringType m with
    | None -> None
    | Some typeName when allowed |> List.exists (fun a -> matches a typeName m) -> None
    | Some typeName -> bans |> List.tryFind (fun b -> matches b.Target typeName m)

let private problem (ban: Ban) (api: string) =
    if ban.Rule.Code = Rules.reflection.Code then
        $"`{api}` is runtime reflection, which Native AOT does not support reliably."
    else
        $"`{api}`: {ban.Rule.Title}. Direct I/O breaks replay and determinism."

/// Findings for the rules in `rules` at the given sites.
let check (rules: Rules.Rule list) (sites: Site list) =
    let inRules (rule: Rules.Rule) =
        rules |> List.exists (fun r -> r.Code = rule.Code)

    [
        for site in sites do
            match site with
            | Call(m, _, range)
            | New(m, _, range) ->
                match banned m with
                | Some ban when inRules ban.Rule -> Rules.finding ban.Rule (problem ban (display m)) range
                | _ -> ()
            | Quotation range when inRules Rules.reflection ->
                Rules.finding
                    Rules.reflection
                    "Quotation literal `<@ ... @>`: quotations (and `query { }`) are rebuilt with runtime reflection, which Native AOT does not support reliably."
                    range
            | Quotation _ -> ()
    ]
