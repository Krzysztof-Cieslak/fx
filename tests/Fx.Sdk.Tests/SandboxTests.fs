/// The sandbox contract (spec §9.1, §7.1): paths, a policy's rules and the access they give, how
/// overrides only tighten, the JSON of policies and overrides, and the checks of drivers. The drivers'
/// own tests are in tests/Fx.Ext.Sandbox*.Tests; the conformance suite in tests/Fx.Environments.Tests.
module Fx.Sdk.Tests.SandboxTests

open Expecto
open FsCheck
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Sdk

let private policy: SandboxPolicy =
    {
        Workspace = "/src/app"
        WorkspaceAccess = WorkspaceAccess.ReadWrite
        Writable = [ "/home/u/.fx/environments/e1/owned" ]
        Readable = [ "/home/u/.fx/runtimes" ]
        Protected = [ "/src/app/.git/hooks"; "/src/app/.git/config" ]
        Denied = [ "/home/u/.fx"; "/home/u/.ssh" ]
        Temp = Some "/home/u/.fx/environments/e1/tmp"
        Network = NetworkAccess.Denied
    }

let private rank (access: PathAccess) =
    match access with
    | PathAccess.NoAccess -> 0
    | PathAccess.ReadOnly -> 1
    | PathAccess.ReadWrite -> 2

let private ready: SandboxReady =
    {
        Driver = "test"
        Program = "/usr/bin/sandbox"
        Version = "1"
        Features = []
        Files = Map.empty
        Aliases = [ "/var/folders/x", "/private/var/folders/x" ]
        Notes = []
    }

[<Tests>]
let tests =
    testList
        "sandbox contract"
        [
            test "paths: absolute, under, between, normalized" {
                Expect.isTrue (SandboxPath.isAbsolute "/a") "Unix"
                Expect.isTrue (SandboxPath.isAbsolute @"C:\a") "Windows"
                Expect.isFalse (SandboxPath.isAbsolute "a/b") "relative"
                Expect.isTrue (SandboxPath.isUnder "/src/app" "/src/app") "itself"
                Expect.isTrue (SandboxPath.isUnder "/src/app" "/src/app/x/y") "inside"
                Expect.isFalse (SandboxPath.isUnder "/src/app" "/src/application") "a longer name is not inside"
                Expect.isTrue (SandboxPath.isUnder "/" "/anything") "the root"
                Expect.equal (SandboxPath.under "/src/app/" "tests/unit") "/src/app/tests/unit" "joined"
                Expect.equal (SandboxPath.resolve "/src/app" "/etc/x") "/etc/x" "absolute stays"

                Expect.equal
                    (SandboxPath.between "/src/app" "/src/app/a/b/c")
                    [ "/src/app/a"; "/src/app/a/b"; "/src/app/a/b/c" ]
                    "between"

                Expect.equal (SandboxPath.between "/src/app" "/src/app") [] "nothing between a path and itself"
                Expect.equal (SandboxPath.normalize "/src/app/../../home/u/.ssh/./id") "/home/u/.ssh/id" "dots"
                Expect.equal (SandboxPath.normalize "/../..") "/" "above the root"
                Expect.equal (SandboxPath.normalize @"C:\a\..\b") @"C:\b" "Windows"
                Expect.isFalse (SandboxPath.isPlainRelative "../x") "no .."
                Expect.isTrue (SandboxPath.isPlainRelative "tests/unit") "plain"
            }

            test "the longest root containing a path decides its access" {
                let access = SandboxPolicy.access policy
                Expect.equal (access "/src/app/main.fs") PathAccess.ReadWrite "the workspace"
                Expect.equal (access "/src/app/.git/hooks/pre-commit") PathAccess.ReadOnly "protected"
                Expect.equal (access "/src/app/.git/HEAD") PathAccess.ReadWrite "beside a protected path"
                Expect.equal (access "/home/u/.fx/credentials") PathAccess.NoAccess "denied"
                Expect.equal (access "/home/u/.fx/runtimes/bun") PathAccess.ReadOnly "readable inside denied"

                Expect.equal
                    (access "/home/u/.fx/environments/e1/owned/k")
                    PathAccess.ReadWrite
                    "writable inside denied"

                Expect.equal (access "/home/u/.fx/environments/e1/tmp") PathAccess.ReadWrite "the temporary directory"
                Expect.equal (access "/home/u/.ssh/id") PathAccess.NoAccess "a secret"
                Expect.equal (access "/usr/bin/git") PathAccess.ReadOnly "everything else is read-only"
            }

            test "a root listed twice keeps its most restrictive access" {
                let twice =
                    { policy with
                        Writable = "/srv/x" :: policy.Writable
                        Denied = "/srv/x/" :: policy.Denied
                    }

                Expect.equal (SandboxPolicy.access twice "/srv/x/y") PathAccess.NoAccess "denied wins"
                Expect.equal (SandboxPolicy.rules twice |> List.filter (fun r -> r.Root = "/srv/x")).Length 1 "once"
            }

            test "rules are ordered shortest first" {
                let roots = SandboxPolicy.rules policy |> List.map _.Root
                Expect.equal roots (roots |> List.sortBy (fun r -> r.Length, r)) "sorted"
                Expect.contains (SandboxPolicy.paths policy) "/src/app/.git/config" "paths name every root"
            }

            test "guarded: the directories between a writable root and a read-only path in it" {
                Expect.equal (SandboxPolicy.guarded policy) [ "/src/app/.git" ] "the protected paths' parent"

                let deeper =
                    { policy with
                        Protected = [ "/src/app/src/tests/golden" ]
                    }

                Expect.equal (SandboxPolicy.guarded deeper) [ "/src/app/src"; "/src/app/src/tests" ] "every level"
            }

            test "overrides only take access away" {
                let reviewer = SandboxPolicy.tighten SandboxOverrides.readOnly policy

                Expect.equal
                    (SandboxPolicy.access reviewer "/src/app/main.fs")
                    PathAccess.ReadOnly
                    "read-only workspace"

                let widened =
                    SandboxPolicy.tighten
                        { SandboxOverrides.none with
                            Workspace = Some WorkspaceAccess.ReadWrite
                            Network = Some NetworkAccess.Allowed
                        }
                        reviewer

                Expect.equal widened.WorkspaceAccess WorkspaceAccess.ReadOnly "read-write does not undo read-only"
                Expect.equal widened.Network NetworkAccess.Denied "allowed does not undo denied"

                let more =
                    SandboxPolicy.tighten
                        { SandboxOverrides.none with
                            Protect = [ "tests" ]
                            Deny = [ ".env"; "/srv/secret" ]
                        }
                        policy

                Expect.equal (SandboxPolicy.access more "/src/app/tests/a.fs") PathAccess.ReadOnly "protected, relative"
                Expect.equal (SandboxPolicy.access more "/src/app/.env") PathAccess.NoAccess "denied, relative"
                Expect.equal (SandboxPolicy.access more "/srv/secret/x") PathAccess.NoAccess "denied, absolute"
            }

            testProperty "tightening never gives a path more access" (fun (readOnly: bool) (deny: bool) (path: int) ->
                let paths =
                    [|
                        "/src/app/x"
                        "/src/app/.env"
                        "/src/app/tests/a"
                        "/home/u/.fx/runtimes/x"
                        "/home/u/.fx/environments/e1/owned/x"
                        "/usr/x"
                    |]

                let p = paths[abs path % paths.Length]

                let overrides =
                    {
                        Workspace =
                            if readOnly then
                                Some WorkspaceAccess.ReadOnly
                            else
                                Some WorkspaceAccess.ReadWrite
                        Network = None
                        Protect = [ "tests" ]
                        Deny = if deny then [ ".env" ] else []
                    }

                rank (SandboxPolicy.access (SandboxPolicy.tighten overrides policy) p)
                <= rank (SandboxPolicy.access policy p))

            test "the default secrets" {
                let secrets = SandboxPolicy.secrets Os.Linux (Some "/home/u")
                Expect.contains secrets "/home/u/.ssh" "ssh"
                Expect.contains secrets "/home/u/.aws" "aws"
                Expect.contains secrets "/run/user" "the session bus"

                Expect.isFalse
                    (SandboxPolicy.secrets Os.Windows None |> List.isEmpty |> not)
                    "none known on Windows without a home"

                Expect.contains SandboxPolicy.protectedInWorkspace Product.projectDirName "the project's directory"
            }

            test "real paths follow the probe's aliases" {
                Expect.equal (Sandbox.realPath ready "/var/folders/x/ws/a") "/private/var/folders/x/ws/a" "aliased"
                Expect.equal (Sandbox.realPath ready "/var/folders/y") "/var/folders/y" "not aliased"

                let p =
                    { policy with
                        Workspace = "/var/folders/x/ws"
                    }

                Expect.contains
                    (Sandbox.pathsToStat ready p)
                    "/private/var/folders/x/ws"
                    "the real path is looked up too"
            }

            test "processes: the command line and TMPDIR" {
                Expect.equal
                    (Sandbox.commandLine ShellSpec.posix (Program.Script "echo hi"))
                    ("/bin/sh", [ "-c"; "echo hi" ])
                    "a script"

                Expect.equal
                    (Sandbox.commandLine ShellSpec.posix (Exec("git", [ "status" ])))
                    ("git", [ "status" ])
                    "a program"

                Expect.equal
                    (string (Sandbox.withTemp policy (Inherit [])))
                    (string (Inherit [ "TMPDIR", Some "/home/u/.fx/environments/e1/tmp" ]))
                    "inherited"

                Expect.equal
                    (string (Sandbox.withTemp policy (Exactly [ "A", "b" ])))
                    (string (Exactly [ "A", "b"; "TMPDIR", "/home/u/.fx/environments/e1/tmp" ]))
                    "added"

                Expect.equal
                    (string (Sandbox.withTemp policy (Exactly [ "TMPDIR", "/owned/tmp" ])))
                    (string (Exactly [ "TMPDIR", "/owned/tmp" ]))
                    "a process's own TMPDIR is kept"
            }

            test "a symbolic link the probe did not resolve cannot be a target" {
                let request: SandboxRequest =
                    {
                        Ready = { ready with Aliases = [] }
                        Policy = policy
                        Paths = Map.ofList [ "/home/u/.ssh", PathKind.Symlink; "/src/app", PathKind.Directory ]
                        Shell = ShellSpec.posix
                        Process =
                            {
                                Program = Program.Script "true"
                                Cwd = None
                                Env = Inherit []
                                Terminal = false
                            }
                    }

                Expect.isError (Sandbox.target request "/home/u/.ssh") "a link"

                Expect.equal
                    (Sandbox.target request "/src/app")
                    (Ok(Some("/src/app", PathKind.Directory)))
                    "a directory"

                Expect.equal (Sandbox.target request "/nowhere") (Ok None) "missing"
            }

            test "policies and overrides as JSON" {
                let json = Codec.encode SandboxPolicy.codec policy
                Expect.equal (Codec.decode SandboxPolicy.codec json) (Ok policy) "a policy round-trips"

                match Json.parse """{"workspace": "read-only", "protect": ["tests"], "deny": [".env"]}""" with
                | Ok json ->
                    Expect.equal
                        (Codec.decode SandboxOverrides.codec json)
                        (Ok
                            { SandboxOverrides.none with
                                Workspace = Some WorkspaceAccess.ReadOnly
                                Protect = [ "tests" ]
                                Deny = [ ".env" ]
                            })
                        "the reviewer's frontmatter"
                | Error e -> failtest (string e)

                for bad in
                    [
                        """{"workspace": "rw"}"""
                        """{"protect": ["../x"]}"""
                        """{"writable": ["/x"]}"""
                    ] do
                    match Json.parse bad with
                    | Ok json -> Expect.isError (Codec.decode SandboxOverrides.codec json) bad
                    | Error e -> failtest (string e)
            }

            test "drivers are checked" {
                let driver: SandboxDriver =
                    {
                        Id = "Bad Id"
                        Description = ""
                        Platforms = []
                        Confines = false
                        Probe = fun _ -> failwith "unused"
                        Wrap = fun _ -> Error "unused"
                    }

                Expect.equal
                    (SandboxDrivers.validate driver |> List.map (fun i -> JsonPath.render i.Path))
                    [ "$.id"; "$.platforms"; "$.confines" ]
                    "id, platforms, confines"

                Expect.isEmpty
                    (SandboxDrivers.validate
                        { driver with
                            Id = "none"
                            Platforms = [ Os.Linux ]
                        })
                    "none confines nothing"

                Expect.isTrue SandboxDrivers.contributionType.Privileged "privileged"
            }
        ]
