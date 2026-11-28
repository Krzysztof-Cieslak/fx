module Fx.Kernel.Tests.ProtocolTests

open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Fakes
open Fx.Effects.Live
open Fx.Kernel
open Fx.Sdk
open Fx.Store

let private roundTrip (codec: Codec<'T>) (value: 'T) =
    let json = codec.Encode value

    match codec.Decode json with
    | Ok decoded -> Expect.equal (Json.serialize (codec.Encode decoded)) (Json.serialize json) "round trip"
    | Error issues -> failtest (Issue.renderAtMost 5 issues)

let private parse (text: string) =
    match Json.parse text with
    | Ok json -> json
    | Error e -> failtest (string e)

let private preview text = Preview.full text

let private handle =
    match HandleId.tryParse "h-0123456789abcdef" with
    | Some id -> id
    | None -> failtest "a handle id"

let private values =
    [
        Value.Inline(parse """{"a": [1, 2.5, "x", null, true]}""")
        Value.Blob(
            {
                Hash = BlobHash.ofBytes [| 1uy |]
                Size = 1L
            },
            preview "one byte"
        )
        Value.Handle(handle, TypeRef "RunView", preview "run 42")
    ]

let private outcomes =
    [
        CellOutcome.Completed None
        CellOutcome.Completed(
            Some
                {
                    Type = "number"
                    Preview = preview "42"
                }
        )
        CellOutcome.SyntaxError
            {
                Message = "unexpected token"
                Line = Some 2
                Column = Some 5
                LineText = Some "let = ;"
            }
        CellOutcome.TypeError
            [
                {
                    Line = 1
                    Column = 3
                    Code = "TS2322"
                    Message = "no"
                }
            ]
        CellOutcome.RuntimeError
            {
                Name = "Error"
                Message = "boom"
                Code = Some "failed"
                Location = Some { CellId = "c1"; Line = 1; Column = 7 }
                Stack = "Error: boom\n    at cell c1:1:7"
            }
        CellOutcome.TimedOut 300
        CellOutcome.Cancelled
    ]

let private errors =
    [
        KernelError.Busy "c1"
        KernelError.DuplicateCell "c1"
        KernelError.InvalidCellId "a b"
        KernelError.UnknownCell "c9"
        KernelError.NotFound(ValueRef.Variable "x")
        KernelError.NotFound(ValueRef.Output "c1")
        KernelError.NotEncodable("function", "no JSON")
        KernelError.TooLarge(100000L, 4096)
        KernelError.InvalidName "1x"
        KernelError.InvalidValue "not a snapshot"
    ]

let private hostErrors =
    [
        HostCallError.UnknownBinding "shell.nope"
        HostCallError.InvalidArguments [ Issue.at [ PathSegment.Index 0 ] "expected string" ]
        HostCallError.UnknownHandle handle
        HostCallError.Denied "no grant"
        HostCallError.Failed("exit", "exit 1", Some(Json.int 1))
        HostCallError.Failed("exit", "exit 1", None)
    ]

[<Tests>]
let tests =
    testList
        "protocol"
        [
            test "values round trip" {
                for value in values do
                    roundTrip ValueCodecs.value value
            }

            test "cell results round trip, for every outcome" {
                for outcome in outcomes do
                    for unassigned in [ []; [ "y" ] ] do
                        roundTrip
                            KernelProtocol.Codecs.cellResult
                            {
                                CellId = "c1"
                                Outcome = outcome
                                Output =
                                    {
                                        Text = "hi\n"
                                        Truncated = false
                                        TotalChars = 3L
                                        Whole = None
                                    }
                                Declared = [ "x"; "y" ]
                                Unassigned = unassigned
                                Finished = false
                                Notified = 1
                                DurationMs = 12L
                            }
            }

            test "unassigned names are absent when there are none: older hosts read the result as before" {
                let result unassigned =
                    {
                        CellId = "c1"
                        Outcome = CellOutcome.Cancelled
                        Output =
                            {
                                Text = ""
                                Truncated = false
                                TotalChars = 0L
                                Whole = None
                            }
                        Declared = [ "x" ]
                        Unassigned = unassigned
                        Finished = false
                        Notified = 0
                        DurationMs = 0L
                    }

                let members (json: Json) =
                    match json with
                    | Json.Object members -> members |> List.map fst
                    | _ -> []

                let none = KernelProtocol.Codecs.cellResult.Encode(result [])
                Expect.isFalse (List.contains "unassigned" (members none)) "absent when empty"

                let some = KernelProtocol.Codecs.cellResult.Encode(result [ "x" ])
                Expect.isTrue (List.contains "unassigned" (members some)) "present otherwise"

                // What a kernel without the field sends.
                match KernelProtocol.Codecs.cellResult.Decode none with
                | Ok decoded -> Expect.equal decoded.Unassigned [] "none"
                | Error issues -> failtest (Issue.renderAtMost 3 issues)
            }

            test "errors round trip and say what happened" {
                for error in errors do
                    roundTrip KernelProtocol.Codecs.kernelError error
                    Expect.isNotEmpty (string error) "a message"

                for error in hostErrors do
                    roundTrip KernelProtocol.Codecs.hostCallError error
                    Expect.isNotEmpty (string error) "a message"
            }

            test "the handshake members round trip" {
                roundTrip
                    KernelProtocol.Codecs.kernelSetup
                    {
                        Versions = [ "1" ]
                        Global = "g"
                        Bindings = [ "shell.run" ]
                        ModuleBase = Some "/work"
                        Limits = KernelProtocol.defaultLimits
                    }

                roundTrip
                    KernelProtocol.Codecs.kernelInfo
                    {
                        Version = "1"
                        Language = "typescript"
                        Caps =
                            {
                                Snapshot = false
                                AsyncHostCalls = AsyncHostCalls.Await
                                Packages = Some { Manager = "bun" }
                                Deterministic = false
                            }
                        Details = Some(Json.Object [ "bun", Json.String "1.4.5" ])
                    }
            }

            test "cell ids are checked by the codec" {
                let decode id =
                    KernelProtocol.Codecs.cellRun.Decode(
                        Json.Object [ "cellId", Json.String id; "code", Json.String "" ]
                    )

                Expect.isOk (decode "c-1_A") "valid"
                Expect.isError (decode "") "empty"
                Expect.isError (decode "a b") "a space"
                Expect.isError (decode (String.replicate 65 "a")) "too long"
            }

            test "only inline values decode into declared types" {
                Expect.equal (Value.decode Codec.int (Value.Inline(Json.int 3))) (Ok 3) "inline"

                for value in values |> List.tail do
                    match Value.decode Codec.int value with
                    | Error [ issue ] ->
                        Expect.stringStarts issue.Message "expected number, got a" "says what was expected"
                    | other -> failtest $"{other.IsOk}"
            }

            test "finish validates with codecs, then the checks" {
                let check (n: int) =
                    if n > 0 then [] else [ Issue.create "must be positive" ]

                let finish json =
                    KernelProtocol.Finish.validate
                        Codec.int
                        [ check ]
                        {
                            CellId = None
                            Result = Value.Inline json
                        }

                Expect.equal (finish (Json.int 2)) (Ok 2) "accepted"

                match finish (Json.String "x") with
                | Error rejected -> Expect.equal rejected.Issues.Head.Path [ PathSegment.Key "result" ] "under result"
                | Ok _ -> failtest "a string as an int"

                match finish (Json.int 0) with
                | Error rejected -> Expect.equal rejected.Issues [ Issue.create "must be positive" ] "the check"
                | Ok _ -> failtest "0 is not positive"
            }

            test "the launch hygiene check refuses inherited and denied environments" {
                let launch env deny ignoreCase =
                    {
                        Process =
                            {
                                Program = Exec("kernel", [])
                                Cwd = None
                                Env = env
                            }
                        EnvDeny = deny
                        IgnoreEnvCase = ignoreCase
                    }

                Expect.isNonEmpty (KernelLaunch.check (launch (Inherit []) [] false)) "inherited"
                Expect.isEmpty (KernelLaunch.check (launch (Exactly [ "HOME", "/h" ]) [] false)) "exact"

                Expect.hasLength
                    (KernelLaunch.check (
                        launch (Exactly [ "NODE_OPTIONS", "x"; "BUN_JSC_x", "1" ]) [ "BUN_JSC_*" ] false
                    ))
                    2
                    "the host's and the runtime's deny lists"

                Expect.isEmpty
                    (KernelLaunch.check (launch (Exactly [ "node_options", "x" ]) [] false))
                    "names are case-sensitive on POSIX"

                Expect.isNonEmpty
                    (KernelLaunch.check (launch (Exactly [ "node_options", "x" ]) [] true))
                    "and not on Windows"
            }

            testTask "handle tables issue unguessable ids and resolve only their own" {
                let env: Fx.Kernel.Tests.KernelConformance.Env =
                    { Fx.Kernel.Tests.KernelConformance.env () with
                        Ids = FakeIds(7UL, SystemClock.clock).Ids
                    }

                let mine = HandleTable<string>()
                let theirs = HandleTable<string>()

                let! value =
                    Eff.run env (HandleTable.issue mine (TypeRef "Thing") (preview "p") "target")

                match value with
                | Value.Handle(id, typeRef, _) ->
                    Expect.equal id.Value.Length (2 + 2 * HandleTable.IdBytes) "128 bits in hex"
                    Expect.equal typeRef (TypeRef "Thing") "type"
                    Expect.equal (HandleTable.resolveAs mine (TypeRef "Thing") id) (Ok "target") "resolves"

                    match HandleTable.resolveAs mine (TypeRef "Other") id with
                    | Error(HandleError.WrongType _) -> ()
                    | other -> failtest $"{other.IsOk}"

                    Expect.isError (HandleTable.resolve theirs id) "another session's table"
                    Expect.isTrue (HandleTable.release mine id) "released"
                    Expect.isError (HandleTable.resolve mine id) "gone"
                | other -> failtest (string other)
            }
        ]

/// Starts the client against an in-process server that answers `initialize` with `members` and
/// handles `methods` (each answering nothing useful).
let private connectTo (members: (string * Json) list) (methods: string list) =
    task {
        let frames = Fx.Rpc.ConnectionLimits.frames KernelOptions.defaults.Limits
        let client, server = Fx.Rpc.InProcess.pair 65536 frames frames

        let serverEnv: Fx.Aot.Scenarios.Rpc.TestService.Env =
            {
                Clock = SystemClock.clock
                Scope = Scope.root
            }

        let handlers =
            methods
            |> List.map (fun name ->
                Fx.Rpc.Handler.request (Fx.Rpc.Method.define name Codec.json Codec.json) (fun _ _ ->
                    Eff.ret (Ok Json.Null)))

        let service =
            Fx.Rpc.Service.create { Name = "fake"; Version = "0" } handlers
            |> Fx.Rpc.Service.withAccept (fun _ -> Eff.ret (Ok members))

        Eff.run serverEnv (Fx.Rpc.Rpc.accept service server) |> ignore
        let env = Fx.Kernel.Tests.KernelConformance.env ()

        let! outcome =
            Eff.run
                env
                (Kernel.connect
                    KernelOptions.defaults
                    KernelHost.empty
                    Fx.Kernel.Tests.KernelConformance.setup
                    (fun () -> System.Threading.Tasks.Task.FromResult())
                    client)

        server.Abort()
        return outcome
    }

let private info version =
    KernelProtocol.Codecs.kernelInfo.Encode
        {
            Version = version
            Language = "fake"
            Caps =
                {
                    Snapshot = false
                    AsyncHostCalls = AsyncHostCalls.Await
                    Packages = None
                    Deterministic = false
                }
            Details = None
        }

[<Tests>]
let clientTests =
    testList
        "client"
        [
            testTask "a peer without the kernel member is not a kernel" {
                match! connectTo [] KernelProtocol.requiredMethods with
                | Error(StartError.Incompatible reason) -> Expect.stringContains reason "kernel" "why"
                | other -> failtest $"{other.IsOk}"
            }

            testTask "a kernel must choose a version the host offered" {
                match! connectTo [ "kernel", info "2" ] KernelProtocol.requiredMethods with
                | Error(StartError.Incompatible reason) -> Expect.stringContains reason "\"2\"" "why"
                | other -> failtest $"{other.IsOk}"
            }

            testTask "a kernel must handle the required methods" {
                match! connectTo [ "kernel", info "1" ] [ "cell/run" ] with
                | Error(StartError.Incompatible reason) -> Expect.stringContains reason "value/encode" "what is missing"
                | other -> failtest $"{other.IsOk}"
            }

            testTask "a launch that fails the hygiene check starts nothing" {
                let env =
                    { Fx.Kernel.Tests.KernelConformance.env () with
                        Shell = (ScriptedShell(fun _ -> failtest "nothing may start")).Shell
                    }

                let launch =
                    {
                        Process =
                            {
                                Program = Exec("kernel", [])
                                Cwd = None
                                Env = Inherit []
                            }
                        EnvDeny = []
                        IgnoreEnvCase = false
                    }

                match!
                    Eff.run
                        env
                        (Kernel.start
                            KernelOptions.defaults
                            KernelHost.empty
                            Fx.Kernel.Tests.KernelConformance.setup
                            launch)
                with
                | Error(StartError.UnsafeLaunch _) -> ()
                | other -> failtest $"{other.IsOk}"
            }
        ]
