module Fx.Components.Tests.PlannerTests

open System
open System.IO
open Expecto
open FsCheck
open Fx
open Fx.Components
open Fx.Components.Tests.Support
open Fx.Sdk

/// Golden files in `golden/` next to these tests; `UPDATE_GOLDEN=1` rewrites them.
module Golden =
    let private directory =
        let rec up (dir: DirectoryInfo | null) =
            match dir with
            | null -> failwith $"no Fx.slnx above {AppContext.BaseDirectory}"
            | dir when File.Exists(Path.Combine(dir.FullName, "Fx.slnx")) -> dir.FullName
            | dir -> up dir.Parent

        Path.Combine(up (DirectoryInfo AppContext.BaseDirectory), "tests", "Fx.Components.Tests", "golden")

    let expect (name: string) (actual: string) =
        let path = Path.Combine(directory, name)

        if Environment.GetEnvironmentVariable "UPDATE_GOLDEN" = "1" then
            Directory.CreateDirectory directory |> ignore
            File.WriteAllText(path, actual)
        else
            Expect.isTrue (File.Exists path) $"missing golden file {path}; run the tests with UPDATE_GOLDEN=1"
            Expect.equal actual (File.ReadAllText path) $"golden/{name} (rerun with UPDATE_GOLDEN=1 to update)"

/// 100 tests per property; set PROPERTY_TESTS to run more.
let private property name test =
    let config =
        { FsCheckConfig.defaultConfig with
            maxTest =
                match Environment.GetEnvironmentVariable "PROPERTY_TESTS" with
                | null -> 100
                | count -> int count
        }

    testPropertyWithConfig config name test

let private linux = TargetTriple.create Os.Linux Arch.X64 (Some Libc.Glibc)
let private musl = TargetTriple.create Os.Linux Arch.Arm64 (Some Libc.Musl)
let private windows = TargetTriple.create Os.Windows Arch.X64 None

let private archive (id: string) (platforms: TargetTriple list) : MethodChoice =
    {
        Platforms = platforms
        Method =
            Archive
                {
                    Url = $"https://example.test/{id}/v${{version}}/{id}.tar.gz"
                    Digest = digestOf (utf8.GetBytes id)
                    Format = ArchiveFormat.TarGz
                    Strip = 1
                }
    }

let private def (id: string) (version: string) (deps: (string * string) list) : Component =
    { Component.create id (semver version) [ archive id [] ] with
        DependsOn = deps |> List.map (fun (id, range) -> { Id = id; Version = range })
    }

let private req (id: string) (range: string) (by: string) : Requirement =
    {
        Ref = { Id = id; Version = range }
        By = by
    }

let private plan (catalog: Component list) (requires: Requirement list) (targets: TargetTriple list) =
    Planner.plan PlanSettings.defaults catalog requires targets

let private ok (result: Result<Lockfile, PlanError list>) =
    match result with
    | Ok lockfile -> lockfile
    | Error errors -> failtest (errors |> List.map string |> String.concat "; ")

let private errors (result: Result<Lockfile, PlanError list>) =
    match result with
    | Ok lockfile -> failtest $"expected errors, planned {Lockfile.render lockfile}"
    | Error errors -> errors

let private versions (lockfile: Lockfile) =
    lockfile.Components |> List.map (fun c -> $"{c.Id} {c.Version}")

let private catalog =
    [
        def "tool" "1.0.0" [ "lib", "^2" ]
        def "tool" "1.5.0" [ "lib", "^2.1" ]
        def "tool" "2.0.0" [ "lib", "^3" ]
        def "lib" "1.9.0" []
        def "lib" "2.0.0" []
        def "lib" "2.1.0" []
        def "lib" "3.0.0" []
        def "zeta" "0.1.0" []
    ]

[<Tests>]
let tests =
    testList
        "Planner and lockfile"
        [
            test "picks the highest version in every range, and resolves dependencies" {
                let lockfile =
                    ok (plan catalog [ req "tool" "^1" "a"; req "tool" ">=1.2" "b" ] [ linux ])

                Expect.equal (versions lockfile) [ "lib 2.1.0"; "tool 1.5.0" ] "tool 1.5.0 and its lib"

                Expect.equal
                    (lockfile.Components |> List.find (fun c -> c.Id = "tool")).DependsOn
                    [ "lib" ]
                    "dependency ids"
            }

            test "dependencies and requirements constrain each other" {
                let lockfile =
                    ok (plan catalog [ req "tool" "1" "a"; req "lib" "<2.1" "b" ] [ linux ])

                Expect.equal
                    (versions lockfile)
                    [ "lib 2.0.0"; "tool 1.0.0" ]
                    "tool 1.5.0 needs lib ^2.1, so tool 1.0.0"
            }

            test "conflicts say who asked for what, and what there is" {
                match errors (plan catalog [ req "tool" "^1" "a"; req "tool" "^2" "b" ] [ linux ]) with
                | [ NoVersion("tool", asked, available) ] ->
                    Expect.equal (asked |> List.map (fun r -> r.By)) [ "a"; "b" ] "both requirers"
                    Expect.equal available [ "1.0.0"; "1.5.0"; "2.0.0" ] "the versions"
                | other -> failtest $"{other}"
            }

            test "unknown components, invalid ranges, cycles and conflicting definitions are errors" {
                match errors (plan catalog [ req "nope" "1" "acme.tools" ] [ linux ]) with
                | [ UnknownComponent("nope", [ "acme.tools" ]) ] -> ()
                | other -> failtest $"{other}"

                match errors (plan catalog [ req "tool" "latest" "a" ] [ linux ]) with
                | [ InvalidRange _ ] -> ()
                | other -> failtest $"{other}"

                match
                    errors (
                        plan
                            [ def "a" "1.0.0" [ "b", "1" ]; def "b" "1.0.0" [ "a", "1" ] ]
                            [ req "a" "1" "x" ]
                            [ linux ]
                    )
                with
                | [ Cycle [ "a"; "b"; "a" ] ] -> ()
                | other -> failtest $"{other}"

                let other =
                    { def "lib" "2.1.0" [] with
                        Size = { Download = Some 1L; Installed = None }
                    }

                match errors (plan (other :: catalog) [ req "lib" "2.1.0" "x" ] [ linux ]) with
                | [ Conflicting("lib", "2.1.0") ] -> ()
                | other -> failtest $"{other}"

                // A duplicate that is identical is fine.
                ok (plan (def "lib" "2.1.0" [] :: catalog) [ req "lib" "2.1.0" "x" ] [ linux ])
                |> ignore
            }

            test "each target gets the first method for it that the installer implements" {
                let bun =
                    { Component.create
                          "bun"
                          (semver "1.4.5")
                          [
                              archive "bun-linux" [ linux ]
                              archive "bun-musl" [ musl ]
                              {
                                  Platforms = []
                                  Method =
                                      System
                                          {
                                              Probe = [ "bun"; "--version" ]
                                              Constraint = "1"
                                          }
                              }
                          ] with
                        Platforms = [ linux; musl; windows ]
                    }

                let lockfile = ok (plan [ bun ] [ req "bun" "1" "x" ] [ musl; linux ])
                let locked = List.exactlyOne lockfile.Components

                Expect.equal
                    (locked.Install |> List.map (fst >> string))
                    [ "linux-arm64-musl"; "linux-x64-glibc" ]
                    "sorted targets"

                match locked.Install with
                | [ _, Archive musl; _, Archive linux ] ->
                    Expect.equal musl.Url "https://example.test/bun-musl/v1.4.5/bun-musl.tar.gz" "expanded"
                    Expect.equal linux.Url "https://example.test/bun-linux/v1.4.5/bun-linux.tar.gz" "expanded"
                | other -> failtest $"{other}"

                match errors (plan [ bun ] [ req "bun" "1" "x" ] [ linux; windows ]) with
                | [ NoMethod("bun", "1.4.5", t, reason) ] ->
                    Expect.equal t windows "Windows"
                    Expect.stringContains reason "\"system\"" "the method it has"
                    Expect.stringContains reason "T211" "who implements it"
                | other -> failtest $"{other}"

                match errors (plan [ bun ] [ req "bun" "1" "x" ] [ TargetTriple.create Os.MacOs Arch.Arm64 None ]) with
                | [ NoMethod(_, _, _, reason) ] -> Expect.stringContains reason "does not support" "platforms"
                | other -> failtest $"{other}"
            }

            test "the lockfile renders as canonical, indented JSON (golden)" {
                // Digests of fixed bytes: compressed archives may differ between zlib builds.
                let tool = toolComponent "1.2.3" (utf8.GetBytes "the tool's archive")

                let lockfile =
                    ok (plan (tool :: catalog) [ req "tool" "1.2" "x"; req "zeta" "*" "y" ] [ windows; linux ])

                let text = Lockfile.render lockfile
                Expect.isTrue (text.EndsWith "}\n") "a final newline"
                Expect.equal (Lockfile.parse text) (Ok lockfile) "round trip"
                Golden.expect "lockfile.json" text
                Expect.equal Lockfile.fileName (Product.Name + ".components.lock") "named after the product"
            }

            test "the hash is of the canonical JSON, whatever the file's formatting" {
                let lockfile = ok (plan catalog [ req "tool" "1" "x" ] [ linux ])
                let hash = Lockfile.hash lockfile
                Expect.stringStarts hash "sha256:" "prefix"
                Expect.equal hash.Length (7 + 64) "length"

                let compact =
                    match Fx.Contracts.Json.parse (Lockfile.render lockfile) with
                    | Ok json -> Fx.Contracts.Json.serialize json
                    | Error e -> failtest (string e)

                match Lockfile.parse compact with
                | Ok parsed -> Expect.equal (Lockfile.hash parsed) hash "same hash"
                | Error e -> failtest e

                let other = ok (plan catalog [ req "tool" "2" "x" ] [ linux ])
                Expect.notEqual (Lockfile.hash other) hash "another lockfile"
            }

            property "the same inputs in any order give the same lockfile, byte for byte"
            <| fun (seed: int) ->
                let random = Random seed

                let shuffle (items: 'T list) =
                    items |> List.sortBy (fun _ -> random.Next())

                let requires =
                    [
                        req "tool" "^1" "a"
                        req "lib" ">=2" "b"
                        req "zeta" "*" "c"
                        req "tool" "1.5" "d"
                    ]

                let targets = [ linux; musl; windows ]
                let expected = Lockfile.render (ok (plan catalog requires targets))

                let actual =
                    Lockfile.render (ok (plan (shuffle catalog) (shuffle requires) (shuffle targets)))

                actual = expected

            test "a lockfile is current for the same requirements, and covers its targets" {
                let requires = [ req "tool" "1" "a"; req "zeta" "*" "b" ]
                let lockfile = ok (plan catalog requires [ linux; windows ])
                let refs = requires |> List.map (fun r -> r.Ref)

                Expect.isTrue
                    (Lockfile.isCurrent (List.rev refs) [ windows ] lockfile)
                    "same requirements, a covered target"

                Expect.isFalse (Lockfile.isCurrent refs [ musl ] lockfile) "a target it lacks"
                Expect.isFalse (Lockfile.isCurrent [ List.head refs ] [ linux ] lockfile) "other requirements"
            }

            test "installs come from the lockfile, dependencies first" {
                let lockfile =
                    ok (plan catalog [ req "tool" "1" "a"; req "zeta" "*" "b" ] [ linux ])

                match Lockfile.resolve [ { Id = "tool"; Version = ">=1.5" } ] lockfile with
                | Ok ids ->
                    match Lockfile.installs linux ids lockfile with
                    | Ok specs ->
                        Expect.equal
                            (specs |> List.map (fun s -> string s.Key |> fun k -> k.Split('-').[0]))
                            [ "lib@2.1.0"; "tool@1.5.0" ]
                            "lib first"

                        Expect.equal (specs |> List.map (fun s -> s.Target)) [ linux; linux ] "for the target"
                    | Error e -> failtest e
                | Error e -> failtest e

                Expect.isError
                    (Lockfile.resolve [ { Id = "tool"; Version = "^2" } ] lockfile)
                    "a version outside the lock"

                Expect.isError
                    (Lockfile.resolve [ { Id = "nope"; Version = "*" } ] lockfile)
                    "a component outside the lock"

                Expect.isError (Lockfile.installs windows [ "tool" ] lockfile) "a target outside the lock"

                let cyclic =
                    { lockfile with
                        Components =
                            lockfile.Components
                            |> List.map (fun c ->
                                if c.Id = "lib" then
                                    { c with DependsOn = [ "tool" ] }
                                else
                                    c)
                    }

                Expect.isError (Lockfile.installs linux [ "tool" ] cyclic) "a cycle in an edited lockfile"
            }

            test "install keys depend on what is installed, not on how it is used" {
                let spec = specOf linux (toolComponent "1.2.3" (toolArchive "1.2.3"))

                let same =
                    InstallSpec.create spec.Id spec.Version spec.Target spec.Method spec.Layout

                Expect.equal same.Key spec.Key "exports and probe are not part of the key"
                Expect.equal spec.Key.Dir $"1.2.3-{spec.Key.Hash}" "the directory name"
                Expect.equal spec.Key.Hash.Length 12 "12 hex digits"

                let other = InstallSpec.create spec.Id spec.Version musl spec.Method spec.Layout
                Expect.notEqual other.Key spec.Key "another target"
            }

            test "a lockfile of another format version is refused" {
                let text = Lockfile.render (ok (plan catalog [ req "zeta" "*" "x" ] [ linux ]))

                match Lockfile.parse (text.Replace("\"lockfileVersion\": 1", "\"lockfileVersion\": 2")) with
                | Error message -> Expect.stringContains message "lockfile version 1" "says which it reads"
                | Ok _ -> failtest "accepted"
            }
        ]
