namespace Fx.Replay

open System
open Fx.Contracts
open Fx.Effects
open Fx.Machines
open Fx.Machines.Host
open Fx.Envd

/// The JSON of journaled values (what capability calls return) and of the inputs whose digests the
/// journal keeps.
[<RequireQualifiedAccess>]
module Values =
    /// `{"ok": …}` or `{"error": …}`.
    let result (ok: Codec<'T>) (error: Codec<'E>) : Codec<Result<'T, 'E>> =
        Codec.union "kind" {
            let! okCase = Codec.case1 "ok" Ok (Field.required "value" ok)
            and! errorCase = Codec.case1 "error" Error (Field.required "error" error)

            return
                fun value ->
                    match value with
                    | Ok v -> okCase v
                    | Error e -> errorCase e
        }

    let instant: Codec<Instant> = Fx.Store.Codecs.instant

    /// Ticks since the clock's origin.
    let mono: Codec<MonoTime> =
        Codec.int64
        |> Codec.map (fun ticks -> MonoTime(TimeSpan.FromTicks ticks)) (fun (MonoTime t) -> t.Ticks)

    let id: Codec<Id> =
        Codec.string
        |> Codec.tryMap
            (fun text ->
                match Id.tryParse text with
                | Some id -> Ok id
                | None -> Error "expected an id")
            string

    let bytes: Codec<byte array> = EnvCodecs.bytes

    let output: Codec<Output> =
        Codec.record {
            let! exitCode = Codec.field "exitCode" (fun (o: Output) -> o.ExitCode) Codec.int
            and! stdout = Codec.field "stdout" (fun o -> o.Stdout) bytes
            and! stderr = Codec.field "stderr" (fun o -> o.Stderr) bytes

            return
                {
                    ExitCode = exitCode
                    Stdout = stdout
                    Stderr = stderr
                }
        }

    let shellError: Codec<ShellError> =
        Codec.union "kind" {
            let! notStarted =
                Codec.case2
                    "notStarted"
                    (fun program reason -> NotStarted(program, reason))
                    (Field.required "program" Codec.string)
                    (Field.required "reason" Codec.string)

            return
                fun value ->
                    match value with
                    | NotStarted(program, reason) -> notStarted program reason
        }

    /// What `Proc.Read` gave.
    let chunk: Codec<OutputChunk option> =
        Codec.union "kind" {
            let! stdout =
                Codec.case1 "stdout" (fun b -> Some(Stdout b)) (Field.required "bytes" bytes)

            and! stderr =
                Codec.case1 "stderr" (fun b -> Some(Stderr b)) (Field.required "bytes" bytes)

            and! ended = Codec.case0 "end" None

            return
                fun value ->
                    match value with
                    | Some(Stdout b) -> stdout b
                    | Some(Stderr b) -> stderr b
                    | None -> ended
        }

    let fileChunk: Codec<FileChunk> =
        Codec.record {
            let! offset = Codec.field "offset" (fun (c: FileChunk) -> c.Offset) Codec.int64
            and! content = Codec.field "content" (fun c -> c.Content) bytes
            and! size = Codec.field "fileSize" (fun c -> c.FileSize) Codec.int64

            return
                {
                    Offset = offset
                    Content = content
                    FileSize = size
                }
        }

    let rejection: Codec<Rejection> =
        Codec.record {
            let! code = Codec.field "code" (fun (r: Rejection) -> r.Code) Codec.string
            and! message = Codec.field "message" (fun r -> r.Message) Codec.string
            return { Code = code; Message = message }
        }

    let appended: Codec<Appended> =
        Codec.record {
            let! after = Codec.field "after" (fun (a: Appended) -> a.After) Codec.int64
            and! count = Codec.field "count" (fun a -> a.Count) Codec.int
            return { After = after; Count = count }
        }

    let appendError: Codec<AppendError> =
        Codec.union "kind" {
            let! conflict =
                Codec.case2
                    "conflict"
                    (fun expected actual -> Conflict(expected, actual))
                    (Field.required "expected" Codec.int64)
                    (Field.required "actual" Codec.int64)

            and! duplicate =
                Codec.case2
                    "duplicate"
                    (fun key appended -> Duplicate(IdempotencyKey key, appended))
                    (Field.required "key" Codec.string)
                    (Field.required "appended" appended)

            return
                fun value ->
                    match value with
                    | Conflict(expected, actual) -> conflict expected actual
                    | Duplicate(key, appended) -> duplicate key.Value appended
        }

    /// A process's inputs as the journal identifies them: the program, the directory and the names
    /// of its variables, never their values (they may hold secrets, spec §18).
    let procInput (spec: ProcSpec) : Json =
        let env =
            match spec.Env with
            | Inherit changes ->
                Json.Object
                    [
                        "inherit",
                        Json.Array(
                            changes
                            |> List.map (fun (name, value) -> Json.String((if value.IsSome then "+" else "-") + name))
                        )
                    ]
            | Exactly vars -> Json.Object [ "exactly", Json.Array(vars |> List.map (fst >> Json.String)) ]

        Json.Object
            [
                "program", EnvCodecs.program.Encode spec.Program
                "cwd",
                (match spec.Cwd with
                 | Some cwd -> Json.String cwd
                 | None -> Json.Null)
                "env", env
            ]

    /// A command's inputs: its process's (`procInput`) and the digest of its standard input.
    let commandInput (command: Command) : Json =
        Json.Object
            [
                "spec", procInput command.Spec
                "stdin", Json.String(Digest.bytes command.Stdin)
            ]

    /// A background job's start as the journal identifies it (T143): its session and id, its command as
    /// a process's inputs (`procInput`: variables by name only) and the digest of its standard input.
    let jobLaunch (launch: Fx.CodeAct.JobLaunch) : Json =
        let spec: ProcSpec =
            {
                Program = Program.Script launch.Command
                Cwd = Some launch.Cwd
                Env = launch.Env
            }

        Json.Object
            [
                "session", Json.String launch.Session
                "id", Json.String launch.Id
                "spec", procInput spec
                "stdin", Json.String(Digest.bytes (Text.Encoding.UTF8.GetBytes(launch.Stdin |> Option.defaultValue "")))
            ]

    let jobRef (job: Fx.CodeAct.JobRef) : Json =
        Json.Object [ "session", Json.String job.Session; "id", Json.String job.Id ]

    let jobRead (read: Fx.CodeAct.JobRead) : Json =
        Json.Object
            [
                "job", jobRef read.Job
                "from",
                (match read.From with
                 | Some offset -> Json.int64 offset
                 | None -> Json.Null)
                "maxChars", Json.int read.MaxChars
            ]

    let jobWait (wait: Fx.CodeAct.JobWait) : Json =
        Json.Object
            [
                "job", jobRef wait.Job
                "timeoutMs",
                (match wait.TimeoutMs with
                 | Some ms -> Json.int ms
                 | None -> Json.Null)
            ]

    /// What a job's start answered.
    let jobStarted: Codec<Result<Fx.CodeAct.JobInfo, string>> =
        result Fx.CodeAct.JobCodecs.info Codec.string

    let jobInfo: Codec<Fx.CodeAct.JobInfo option> =
        Codec.option Fx.CodeAct.JobCodecs.info

    let jobInfos: Codec<Fx.CodeAct.JobInfo list> = Codec.list Fx.CodeAct.JobCodecs.info

    let jobOutput: Codec<Fx.CodeAct.JobOutput option> =
        Codec.option Fx.CodeAct.JobCodecs.output
