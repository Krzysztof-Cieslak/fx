/// Host calls as grants (`Permissions`): what each core binding's calls need, on sh and PowerShell
/// workplaces, with the allowlist, protected paths, working directories and opaque lines, and which
/// commands auto approvals may run (`InWorkspace`, T134).
module Fx.CodeAct.Tests.PermissionTests

open Expecto
open Fx.Contracts
open Fx.Kernel
open Fx.Interpreters
open Fx.Sdk
open Fx.Grants
open Fx.CodeAct

let private unix = Workplace.create "/home/me/ws" Os.Linux
let private windows = Workplace.create "C:\\Users\\me\\ws" Os.Windows

let private call (binding: string) (args: Json list) : HostCall =
    {
        CellId = Some "t1-s1-1"
        Binding = binding
        Args = args |> List.map Value.Inline
    }

let private show (needs: CallNeeds) : string list =
    match needs with
    | CallNeeds.Refused reason -> [ "refused: " + reason ]
    | CallNeeds.Needs needs ->
        needs
        |> List.map (fun n -> n.Action.Value + " " + n.Resource + (if n.Listed then " (listed)" else ""))

let private needsOf (place: Workplace) (binding: string) (args: Json list) : CallNeeds =
    Permissions.needs Permissions.core GrantSettings.defaults place (call binding args)

let private run (place: Workplace) (line: string) : string list =
    show (needsOf place "shell.run" [ Json.String line ])

let private runIn (place: Workplace) (cwd: string) (line: string) : string list =
    show (needsOf place "shell.run" [ Json.String line; Json.Object [ "cwd", Json.String cwd ] ])

/// What a line needs, with what auto approvals may run (`confined`).
let private confined (place: Workplace) (line: string) : string list =
    match needsOf place "shell.run" [ Json.String line ] with
    | CallNeeds.Refused reason -> [ "refused: " + reason ]
    | CallNeeds.Needs needs ->
        needs
        |> List.map (fun n ->
            n.Action.Value
            + " "
            + n.Resource
            + (if n.Listed then " (listed)" else "")
            + (if n.InWorkspace then " (in workspace)" else ""))

let private single (needs: CallNeeds) : GrantNeed =
    match needs with
    | CallNeeds.Needs [ need ] -> need
    | other -> failtest $"expected one need, got {other}"

[<Tests>]
let tests =
    testList
        "Permissions"
        [
            test "a background job's command needs what shell.run of it needs (T143)" {
                let start (place: Workplace) (args: Json list) = show (needsOf place "jobs.start" args)

                Expect.equal
                    (start unix [ Json.String "dotnet test && git push" ])
                    (run unix "dotnet test && git push")
                    "each command"

                Expect.equal
                    (start unix [ Json.String "make > ../out.txt"; Json.Object [ "name", Json.String "build" ] ])
                    (run unix "make > ../out.txt")
                    "its redirections, with options"

                Expect.equal
                    (start unix [ Json.String "touch ../x"; Json.Object [ "cwd", Json.String "sub" ] ])
                    (runIn unix "sub" "touch ../x")
                    "in its working directory"

                Expect.equal
                    (start windows [ Json.String "Remove-Item -Recurse build" ])
                    (run windows "Remove-Item -Recurse build")
                    "PowerShell on Windows"

                let auto (args: Json list) =
                    match needsOf unix "jobs.start" args with
                    | CallNeeds.Needs needs -> needs |> List.map (fun n -> n.InWorkspace)
                    | CallNeeds.Refused reason -> failtest reason

                Expect.equal (auto [ Json.String "npm test" ]) [ true ] "auto approvals run it in the workspace"
                Expect.equal (auto [ Json.String "curl https://x.dev" ]) [ false ] "askCommands still ask"

                Expect.equal
                    (start unix [ Json.String "make"; Json.Object [ "cwd", Json.int 3 ] ])
                    []
                    "options that do not decode: the binding refuses them"
            }

            test "reads under the blob store's directory need no grant; writes there do (T201a)" {
                let blobs = "/home/me/.fx/envd/abc/envd/blobs"
                let place = unix |> Workplace.readable [ blobs ]
                let file = blobs + "/sha256/ab/cdef"

                Expect.equal (show (needsOf place "fs.read" [ Json.String file ])) [] "fs.read of a blob's file"

                Expect.equal
                    (show (needsOf unix "fs.read" [ Json.String file ]))
                    [ "fs.read " + file ]
                    "without the directory, a read outside"

                Expect.equal
                    (show (needsOf place "fs.read" [ Json.String "/home/me/.fx/envd/abc/envd/blobs2/x" ]))
                    [ "fs.read /home/me/.fx/envd/abc/envd/blobs2/x" ]
                    "a sibling with the same prefix is outside"

                Expect.equal
                    (confined place ("grep -n needle " + file))
                    [ $"shell.exec grep -n needle {file} (listed) (in workspace)" ]
                    "a listed search of it runs"

                Expect.equal
                    (confined place ("sed -n 1,5p < " + file))
                    [ "shell.exec sed -n 1,5p (in workspace)" ]
                    "reading it as input"

                Expect.equal
                    (show (needsOf place "fs.write" [ Json.String file; Json.String "x" ]))
                    [ "fs.write " + file ]
                    "a write there is a write outside the workspace"
            }

            test
                "each simple command of a line is a shell.exec; listed ones written plainly in the workspace need no person" {
                Expect.equal (run unix "ls -la") [ "shell.exec ls -la (listed)" ] "listed"
                Expect.equal (run unix "mkdir a && npm test") [ "shell.exec mkdir a"; "shell.exec npm test" ] "split"
                Expect.equal (run unix "make; make") [ "shell.exec make" ] "once per resource"

                Expect.equal
                    (run unix "cat src/a.fs | grep x")
                    [ "shell.exec cat src/a.fs (listed)"; "shell.exec grep x (listed)" ]
                    "a pipeline"

                Expect.equal
                    (run unix "cat *.fs ./a/../b")
                    [ "shell.exec cat *.fs ./a/../b (listed)" ]
                    "names in the workspace"

                Expect.equal
                    (run unix "echo $(rm -rf build)")
                    [ "shell.exec rm -rf build"; "shell.exec echo $(rm -rf build)" ]
                    "a substitution"

                Expect.equal (run unix "") [] "nothing to run"
            }

            test "a listed command is not listed when it may reach outside the workspace" {
                Expect.equal (run unix "cat /etc/passwd") [ "shell.exec cat /etc/passwd" ] "an absolute path"
                Expect.equal (run unix "cat ../secret") [ "shell.exec cat ../secret" ] "above the workspace"
                Expect.equal (run unix "cat -f/etc/x") [ "shell.exec cat -f/etc/x" ] "an option with a path"

                Expect.equal
                    (run unix "grep --file=../x y")
                    [ "shell.exec grep --file=../x y" ]
                    "an option with a value"

                Expect.equal (run unix "cat $HOME/.ssh/id_rsa") [ "shell.exec cat $HOME/.ssh/id_rsa" ] "an expansion"
                Expect.equal (run unix "ls ~") [ "shell.exec ls ~" ] "a tilde"
                Expect.equal (run unix "cat {/etc/passwd,x}") [ "shell.exec cat {/etc/passwd,x}" ] "braces"
                Expect.equal (run unix "FOO=1 ls") [ "shell.exec FOO=1 ls" ] "an assignment"

                Expect.equal
                    (run unix "cd /etc && cat passwd")
                    [ "shell.exec cat passwd" ]
                    "after a change of directory outside"

                Expect.equal (runIn unix "/tmp" "ls") [ "shell.exec ls" ] "run outside the workspace"
                Expect.equal (runIn unix "sub" "ls") [ "shell.exec ls (listed)" ] "run in the workspace"
            }

            test "a cd to a plain path keeps the directories later commands may run in" {
                Expect.equal (run unix "cd src && cat a.txt") [ "shell.exec cat a.txt (listed)" ] "into the workspace"
                Expect.equal (run unix "cd src; cd lib; ls") [ "shell.exec ls (listed)" ] "deeper"
                Expect.equal (run unix "cd .. && ls") [ "shell.exec ls" ] "above the workspace"
                Expect.equal (run unix "cd && ls") [ "shell.exec ls" ] "home: unknown"
                Expect.equal (run unix "cd - && ls") [ "shell.exec ls" ] "back: unknown"
                Expect.equal (run unix "cd $DIR && ls") [ "shell.exec ls" ] "an expansion: unknown"

                // A subshell's cd may have been undone: an argument is checked from every directory.
                Expect.equal
                    (run unix "(cd src && make) ; cat ../x")
                    [ "shell.exec make"; "shell.exec cat ../x" ]
                    "a subshell"

                Expect.equal (run unix "pushd src && ls && popd") [ "shell.exec ls (listed)" ] "pushd and popd"

                Expect.equal
                    (run windows "Set-Location src; Get-ChildItem")
                    [ "shell.exec Get-ChildItem (listed)" ]
                    "PowerShell"

                Expect.equal
                    (run unix "cd src && echo hi > out.txt")
                    [ "shell.exec cd src && echo hi > out.txt" ]
                    "a relative redirection after cd: the whole line"
            }

            test "auto approvals may run commands written plainly in the workspace, not downloads or installs" {
                Expect.equal
                    (confined unix "mkdir a && dotnet test 2>&1 | tail -n 5")
                    [
                        "shell.exec mkdir a (in workspace)"
                        "shell.exec dotnet test (in workspace)"
                        "shell.exec tail -n 5 (listed) (in workspace)"
                    ]
                    "plain commands"

                Expect.equal
                    (confined unix "cd tests && dotnet test")
                    [ "shell.exec dotnet test (in workspace)" ]
                    "in a directory of the workspace"

                Expect.equal (confined unix "curl -s https://x.org") [ "shell.exec curl -s https://x.org" ] "a download"

                Expect.equal
                    (confined unix "env CI=1 timeout 60 /usr/bin/curl x")
                    [ "shell.exec env CI=1 timeout 60 /usr/bin/curl x" ]
                    "past assignments, wrappers and a directory"

                Expect.equal (confined unix "NO_COLOR=1 wget x") [ "shell.exec NO_COLOR=1 wget x" ] "an assignment"
                Expect.equal (confined unix "git push origin main") [ "shell.exec git push origin main" ] "a push"

                Expect.equal
                    (confined unix "git status")
                    [ "shell.exec git status (listed) (in workspace)" ]
                    "git status"

                Expect.equal (confined unix "npm install") [ "shell.exec npm install" ] "an install"

                Expect.equal
                    (confined unix "bun install --frozen-lockfile")
                    [ "shell.exec bun install --frozen-lockfile" ]
                    "bun"

                Expect.equal
                    (confined unix "dotnet add src/a.fsproj package Foo")
                    [ "shell.exec dotnet add src/a.fsproj package Foo" ]
                    "a package added"

                Expect.equal
                    (confined unix "bash -c 'rm -rf x'")
                    [ "shell.exec bash -c rm -rf x" ]
                    "a shell's inline line"

                Expect.equal
                    (confined unix "echo $(curl x)")
                    [ "shell.exec curl x"; "shell.exec echo $(curl x)" ]
                    "a substitution"

                Expect.equal (confined unix "cat /etc/passwd") [ "shell.exec cat /etc/passwd" ] "an argument outside"
                Expect.equal (confined unix "make $TARGET") [ "shell.exec make $TARGET" ] "an expansion"
                Expect.equal (confined unix "cd .. && make") [ "shell.exec make" ] "run outside the workspace"

                Expect.equal
                    (confined unix "make > /tmp/log")
                    [ "shell.exec make (in workspace)"; "fs.write /tmp/log" ]
                    "a write outside still needs fs.write"

                Expect.equal
                    (confined unix "for f in *; do rm $f; done")
                    [ "shell.exec for f in *; do rm $f; done" ]
                    "an opaque line"

                Expect.equal (confined windows "dotnet build") [ "shell.exec dotnet build (in workspace)" ] "PowerShell"

                Expect.equal
                    (confined windows "Invoke-WebRequest https://x.org -OutFile a")
                    [ "shell.exec Invoke-WebRequest https://x.org -OutFile a" ]
                    "a PowerShell download"

                Expect.equal (confined windows "IWR x") [ "shell.exec IWR x" ] "any case"
            }

            test "a listed command that reads outside the workspace or an excluded file is not listed" {
                Expect.equal (run unix "cat < a.txt") [ "shell.exec cat (listed)" ] "an input in the workspace"
                Expect.equal (run unix "cat < /etc/shadow") [ "shell.exec cat" ] "an input outside"
                Expect.equal (run unix "cat < $F") [ "shell.exec cat" ] "an input the shell names"

                let excluding =
                    match
                        ContentExclusions.compile
                            { ContentExclusions.none with
                                Lists =
                                    [
                                        {
                                            Origin = "host"
                                            Patterns = [ ".env"; "secrets/" ]
                                        }
                                    ]
                            }
                    with
                    | Ok exclusions ->
                        { GrantSettings.defaults with
                            Exclusions = exclusions
                        }
                    | Error issues -> failtest $"{issues}"

                let runWith (line: string) =
                    show (Permissions.needs Permissions.core excluding unix (call "shell.run" [ Json.String line ]))

                Expect.equal (runWith "cat .env") [ "shell.exec cat .env" ] "an excluded file"

                Expect.equal
                    (runWith "grep -r key secrets/a.txt")
                    [ "shell.exec grep -r key secrets/a.txt" ]
                    "under an excluded directory"

                Expect.equal (runWith "cat < .env") [ "shell.exec cat" ] "an excluded input"
                Expect.equal (runWith "cat README.md") [ "shell.exec cat README.md (listed)" ] "other files"
            }

            test "redirections write files: outside the workspace and to protected paths they need fs.write" {
                Expect.equal
                    (run unix "echo hi > out.txt 2>/dev/null")
                    [ "shell.exec echo hi (listed)" ]
                    "in the workspace"

                Expect.equal
                    (run unix "echo hi > /tmp/x")
                    [ "shell.exec echo hi (listed)"; "fs.write /tmp/x" ]
                    "outside"

                Expect.equal
                    (run unix "echo hi >> .git/hooks/pre-commit")
                    [ "shell.exec echo hi (listed)"; "fs.write .git/hooks/pre-commit" ]
                    "protected"

                Expect.equal
                    (runIn unix "/tmp" "echo hi > x")
                    [ "shell.exec echo hi"; "fs.write /tmp/x" ]
                    "relative to the working directory"

                Expect.equal
                    (run unix "cd /tmp && echo hi > x")
                    [ "shell.exec cd /tmp && echo hi > x" ]
                    "after cd: the whole line"

                Expect.equal
                    (run unix "echo hi > $F")
                    [ "shell.exec echo hi > $F" ]
                    "an expanded target: the whole line"
            }

            test "lines the parser does not follow are one command, with why" {
                let need =
                    single (needsOf unix "shell.run" [ Json.String "for f in *; do\n  rm $f\ndone" ])

                Expect.equal need.Resource "for f in *; do rm $f done" "normalized"
                Expect.isFalse need.Listed "never listed"
                Expect.equal need.Suggestions [] "no broader pattern"

                Expect.isTrue
                    (need.Evidence
                     |> List.exists (fun e -> e.Label = "not split into commands" && e.Text = "control flow (for)"))
                    "why"

                let many = String.concat "; " [ for i in 1..20 -> $"step{i}" ]
                Expect.equal (run unix many) [ "shell.exec " + many ] "more than MaxNeeds"
            }

            test "requests carry summaries, evidence and broader patterns that match" {
                let need =
                    single (needsOf unix "shell.run" [ Json.String "npm run test -- --watch" ])

                Expect.equal need.Summary "Run `npm run test -- --watch`" "summary"
                Expect.equal need.Suggestions [ "npm run **"; "npm **" ] "suggestions"

                let multi = needsOf unix "shell.run" [ Json.String "mkdir a && rm -r b" ]

                match multi with
                | CallNeeds.Needs [ first; _ ] ->
                    Expect.equal
                        first.Evidence
                        [
                            {
                                Label = "command line"
                                Text = "mkdir a && rm -r b"
                            }
                        ]
                        "the whole line"
                | other -> failtest $"{other}"

                let star = single (needsOf unix "shell.run" [ Json.String "rm *.tmp x" ])
                Expect.equal star.Suggestions [ "rm \\*.tmp **"; "rm **" ] "wildcards in the words are escaped"

                let cwd =
                    single (
                        needsOf unix "shell.run" [ Json.String "make"; Json.Object [ "cwd", Json.String "src/app" ] ]
                    )

                Expect.equal cwd.Place "src/app" "the working directory is the place"
            }

            test "fs.write, fs.patch and fs.read: outside the workspace and protected paths" {
                Expect.equal
                    (show (needsOf unix "fs.write" [ Json.String "a.txt"; Json.String "x" ]))
                    []
                    "in the workspace"

                Expect.equal
                    (show (needsOf unix "fs.write" [ Json.String "/etc/hosts"; Json.String "x" ]))
                    [ "fs.write /etc/hosts" ]
                    "outside"

                Expect.equal
                    (show (needsOf unix "fs.write" [ Json.String "../../x"; Json.String "x" ]))
                    [ "fs.write /home/x" ]
                    "above it"

                Expect.equal
                    (show (needsOf unix "fs.write" [ Json.String "/home/me/ws/./b/../c"; Json.String "x" ]))
                    []
                    "absolute, in it"

                Expect.equal
                    (show (needsOf unix "fs.write" [ Json.String ".GIT/config"; Json.String "x" ]))
                    [ "fs.write .GIT/config" ]
                    "protected, any case"

                Expect.equal
                    (show (
                        needsOf
                            unix
                            "fs.write"
                            [ Json.String $"{Fx.Product.projectDirName}/config.toml"; Json.String "x" ]
                    ))
                    [ $"fs.write {Fx.Product.projectDirName}/config.toml" ]
                    "the project's configuration"

                let hook =
                    single (needsOf unix "fs.write" [ Json.String ".git/hooks/pre-commit"; Json.String "x" ])

                Expect.equal hook.Suggestions [ ".git/hooks/**" ] "its directory"
                Expect.equal hook.Place ".git/hooks/pre-commit" "the place"

                let patches =
                    Json.Array
                        [
                            CoreBindings.Codecs.filePatch.Encode(Fx.Envd.FilePatch.Create("a.txt", "x"))
                            CoreBindings.Codecs.filePatch.Encode(Fx.Envd.FilePatch.Delete("/tmp/b", None))
                            CoreBindings.Codecs.filePatch.Encode(Fx.Envd.FilePatch.Edit("/etc/c", None, []))
                        ]

                Expect.equal
                    (show (needsOf unix "fs.patch" [ patches ]))
                    [ "fs.write /tmp/b"; "fs.write /etc/c" ]
                    "each file"

                Expect.equal
                    (show (needsOf unix "fs.read" [ Json.String "/etc/passwd" ]))
                    [ "fs.read /etc/passwd" ]
                    "a read outside"

                Expect.equal (show (needsOf unix "fs.read" [ Json.String "src/a.fs" ])) [] "a read in the workspace"

                let changes = Json.Object [ "old", Json.String "a"; "new", Json.String "b" ]

                Expect.equal
                    (show (needsOf unix "fs.edit" [ Json.String "src/a.fs"; changes ]))
                    []
                    "an edit in the workspace"

                Expect.equal
                    (show (needsOf unix "fs.edit" [ Json.String "/etc/hosts"; changes ]))
                    [ "fs.read /etc/hosts"; "fs.write /etc/hosts" ]
                    "an edit outside: its lines come back, so a read and a write"

                Expect.equal
                    (show (needsOf unix "fs.edit" [ Json.String ".git/config"; changes ]))
                    [ "fs.write .git/config" ]
                    "an edit of a protected path"

                let range (path: string) =
                    Json.Object
                        [
                            "path", Json.String path
                            "start", Json.int 1
                            "end", Json.int 1
                            "text", Json.String "a\n"
                            "digest", Json.String(string (Fx.Store.BlobHash.ofBytes [| 1uy |]))
                            "lines", Json.int 1
                        ]

                Expect.equal
                    (show (needsOf unix "fs.editRange" [ range "src/a.fs"; Json.String "b" ]))
                    []
                    "a range in it"

                Expect.equal
                    (show (needsOf unix "fs.editRange" [ range "../../x"; Json.String "b" ]))
                    [ "fs.read /home/x"; "fs.write /home/x" ]
                    "a range outside"

                Expect.equal
                    (show (needsOf unix "fs.editRange" [ Json.int 1; Json.String "b" ]))
                    []
                    "not a range: refused by the binding"

                Expect.equal (show (needsOf unix "fs.list" [ Json.String "/" ])) [] "listing needs nothing"
            }

            test "arguments that do not decode need nothing: the binding refuses them" {
                Expect.equal (show (needsOf unix "shell.run" [ Json.int 1 ])) [] "a number"
                Expect.equal (show (needsOf unix "shell.run" [ Json.String "rm x"; Json.int 1 ])) [] "bad options"
                Expect.equal (show (needsOf unix "fs.write" [])) [] "no path"
            }

            test "Windows: PowerShell lines, drive and share paths" {
                Expect.equal
                    (run windows "Get-Content .\\a.txt")
                    [ "shell.exec Get-Content .\\a.txt (listed)" ]
                    "listed"

                Expect.equal
                    (run windows "Get-Content C:\\Windows\\x")
                    [ "shell.exec Get-Content C:\\Windows\\x" ]
                    "another place"

                Expect.equal
                    (run windows "Get-Content -Path:C:\\x")
                    [ "shell.exec Get-Content -Path:C:\\x" ]
                    "an option with a path"

                Expect.equal
                    (run windows "dotnet build; dotnet test")
                    [ "shell.exec dotnet build"; "shell.exec dotnet test" ]
                    "split"

                Expect.equal
                    (run windows "echo hi > D:\\out.txt")
                    [ "shell.exec echo hi (listed)"; "fs.write D:/out.txt" ]
                    "a drive"

                Expect.equal (run windows "echo hi > $null") [ "shell.exec echo hi (listed)" ] "the null device"

                Expect.equal
                    (show (needsOf windows "fs.write" [ Json.String "c:\\users\\ME\\ws\\x.txt"; Json.String "x" ]))
                    []
                    "case-insensitive workspace"

                match needsOf windows "fs.write" [ Json.String "\\\\server\\share\\x"; Json.String "x" ] with
                | CallNeeds.Refused reason -> Expect.stringContains reason "cannot be named in a grant" "a share"
                | other -> failtest $"{other}"
            }

            test "network bindings need net.connect for each host and port" {
                let fetch =
                    Permissions.connects "net.fetch" (fun call ->
                        match call.Args with
                        | [ Value.Inline(Json.String url) ] -> Permissions.endpoint url |> Result.map List.singleton
                        | _ -> Error "net.fetch takes a URL")

                let rules = fetch :: Permissions.core

                let need =
                    single (
                        Permissions.needs
                            rules
                            GrantSettings.defaults
                            unix
                            (call "net.fetch" [ Json.String "https://API.GitHub.com/repos" ])
                    )

                Expect.equal need.Action Permissions.netConnect "net.connect"
                Expect.equal need.Resource "api.github.com:443" "host and port"
                Expect.equal need.Suggestions [ "api.github.com:*" ] "any port"
                Expect.equal (Permissions.endpoint "http://localhost:8080/x") (Ok "localhost:8080") "a port"
                Expect.isError (Permissions.endpoint "not a url") "not a URL"

                match Permissions.needs rules GrantSettings.defaults unix (call "net.fetch" [ Json.int 3 ]) with
                | CallNeeds.Refused reason -> Expect.equal reason "net.fetch takes a URL" "refused"
                | other -> failtest $"{other}"
            }

            test "attribute: calls without a cell are seen as the tool call's, the call itself unchanged" {
                let seen = ResizeArray<string option>()

                let guard (call: HostCall) =
                    seen.Add call.CellId
                    call.CellId

                let attributed = Permissions.attribute "t1-s2-3" guard

                Expect.equal
                    (attributed { call "fs.read" [] with CellId = None })
                    (Some "t1-s2-3")
                    "a direct tool's call"

                Expect.equal (attributed (call "fs.read" [])) (Some "t1-s1-1") "a cell's call keeps its cell"
            }

            test "the slots: privileged, with valid patterns" {
                Expect.isTrue GrantSlots.mode.Privileged "mode"
                Expect.isTrue GrantSlots.allowCommands.Privileged "allowed commands"
                Expect.isTrue GrantSlots.askCommands.Privileged "commands auto approvals leave to people"

                for pattern in GrantSlots.defaultAskCommands do
                    Expect.equal (Pattern.validate ResourceKind.Command pattern) (Ok()) pattern

                Expect.equal (GrantSlots.mode.Codec.Decode(Json.String "auto")) (Ok GrantMode.Auto) "auto is a mode"
                Expect.isTrue GrantSlots.protectedPaths.Privileged "protected paths"
                Expect.isFalse GrantSlots.timeoutMs.Privileged "a timeout only tightens"

                for pattern in GrantSlots.defaultCommands do
                    Expect.equal (Pattern.validate ResourceKind.Command pattern) (Ok()) pattern

                Expect.isError
                    (GrantSlots.allowCommands.Codec.Decode(Json.Array [ Json.String "a  b" ]))
                    "an invalid pattern"

                for slot in GrantSlots.all do
                    Expect.isTrue
                        (CodeActSlots.read |> List.exists (fun s -> s.Id = slot.Id))
                        $"{slot.Id} is read by turns"
            }
        ]
