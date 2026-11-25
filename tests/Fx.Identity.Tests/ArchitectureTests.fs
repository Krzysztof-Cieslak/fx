/// Fx.Identity is shared with Fable clients (spec §17): they decode `initialize`'s identity members and
/// read principal ids with it. It may use FSharp.Core, the Fable-compatible Fx.Contracts, and the few
/// base types every F# program compiles to, and nothing else: no tasks, threads, I/O or reflection.
module Fx.Identity.Tests.ArchitectureTests

open System
open System.IO
open System.Reflection.Metadata
open System.Reflection.PortableExecutable
open Expecto

/// The assemblies and types Fx.Identity.dll references, read from its metadata.
let private references () =
    let path = Path.Combine(AppContext.BaseDirectory, "Fx.Identity.dll")
    use stream = File.OpenRead path
    use pe = new PEReader(stream)
    let reader = pe.GetMetadataReader()

    let assemblies =
        [
            for handle in reader.AssemblyReferences do
                reader.GetString(reader.GetAssemblyReference(handle).Name)
        ]

    let types =
        [
            for handle in reader.TypeReferences do
                let reference = reader.GetTypeReference handle
                let ns = reader.GetString reference.Namespace
                let name = reader.GetString reference.Name
                // Nested types have no namespace of their own: name them by the outer type.
                if ns = "" then
                    match reference.ResolutionScope.Kind with
                    | HandleKind.TypeReference ->
                        let outer =
                            reader.GetTypeReference(TypeReferenceHandle.op_Explicit reference.ResolutionScope)

                        $"{reader.GetString outer.Namespace}.{reader.GetString outer.Name}+{name}"
                    | _ -> name
                else
                    $"{ns}.{name}"
        ]

    assemblies, types

/// What a Fable-compatible F# library compiles to: FSharp.Core, the base types, and the interfaces
/// and attributes the compiler generates for records and unions (equality, comparison, debugging).
let private allowedTypes =
    set
        [
            "System.Object"
            "System.String"
            "System.Char"
            // Tuples of every size: the codec builders merge fields and cases into them.
            "System.Tuple`1"
            "System.Tuple`2"
            "System.Tuple`3"
            "System.Tuple`4"
            "System.Tuple`5"
            "System.Tuple`6"
            "System.Tuple`7"
            "System.Tuple`8"
            // `invalidArg`, for defects in how a request kind is defined.
            "System.ArgumentException"
            // `string` formats numbers with the invariant culture.
            "System.IFormattable"
            "System.IFormatProvider"
            "System.Globalization.CultureInfo"
            "System.Boolean"
            "System.Int32"
            "System.Int64"
            "System.ValueType"
            "System.IComparable"
            "System.IComparable`1"
            "System.IEquatable`1"
            "System.Collections.IComparer"
            "System.Collections.IEqualityComparer"
            "System.Collections.IStructuralComparable"
            "System.Collections.IStructuralEquatable"
            "System.Collections.Generic.IComparer`1"
            "System.Collections.Generic.IEqualityComparer`1"
            "System.Collections.Generic.IEnumerable`1"
            "System.Collections.Generic.IEnumerator`1"
            "System.Collections.IEnumerator"
            "System.IDisposable"
            "System.Diagnostics.DebuggableAttribute"
            "System.Diagnostics.DebuggableAttribute+DebuggingModes"
            "System.Diagnostics.DebuggerBrowsableAttribute"
            "System.Diagnostics.DebuggerBrowsableState"
            "System.Diagnostics.DebuggerDisplayAttribute"
            "System.Diagnostics.DebuggerNonUserCodeAttribute"
            "System.Diagnostics.DebuggerStepThroughAttribute"
            "System.Diagnostics.DebuggerHiddenAttribute"
            // Generated for unions; their constructors take a `Type`.
            "System.Diagnostics.DebuggerTypeProxyAttribute"
            "System.Diagnostics.CodeAnalysis.DynamicDependencyAttribute"
            "System.Type"
            "System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes"
            "System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembersAttribute"
            "System.Runtime.CompilerServices.CompilationRelaxationsAttribute"
            "System.Runtime.CompilerServices.RuntimeCompatibilityAttribute"
            "System.Runtime.CompilerServices.CompilerGeneratedAttribute"
            "System.Runtime.CompilerServices.IsReadOnlyAttribute"
            "System.Runtime.CompilerServices.NullableAttribute"
            "System.Runtime.CompilerServices.NullableContextAttribute"
            "System.Runtime.Versioning.TargetFrameworkAttribute"
            "System.Reflection.AssemblyCompanyAttribute"
            "System.Reflection.AssemblyConfigurationAttribute"
            "System.Reflection.AssemblyFileVersionAttribute"
            "System.Reflection.AssemblyInformationalVersionAttribute"
            "System.Reflection.AssemblyProductAttribute"
            "System.Reflection.AssemblyTitleAttribute"
            "System.Reflection.AssemblyVersionAttribute"
            "System.Reflection.AssemblyMetadataAttribute"
            // The package's description (Fx.Identity is an SDK package).
            "System.Reflection.AssemblyDescriptionAttribute"
        ]

[<Tests>]
let tests =
    testList
        "Architecture"
        [
            test "Fx.Identity references only FSharp.Core, the runtime and Fx.Contracts" {
                let assemblies, _ = references ()

                Expect.equal
                    (assemblies
                     |> List.filter (fun name ->
                         not (List.contains name [ "FSharp.Core"; "System.Runtime"; "Fx.Contracts" ])))
                    []
                    "assemblies"
            }

            test
                "Fx.Identity uses only FSharp.Core, Fable-compatible projects and base types (no tasks, threads, I/O or reflection)" {
                let _, types = references ()

                let ours (name: string) =
                    [ "Fx.Identity."; "Fx.Contracts." ]
                    |> List.exists (fun prefix -> name.StartsWith(prefix, StringComparison.Ordinal))

                let foreign =
                    types
                    |> List.filter (fun name ->
                        not (ours name)
                        && not (name.StartsWith("Microsoft.FSharp.", StringComparison.Ordinal))
                        && not (allowedTypes.Contains name))
                    |> List.distinct
                    |> List.sort

                Expect.equal foreign [] "types outside the allowlist"
            }
        ]
