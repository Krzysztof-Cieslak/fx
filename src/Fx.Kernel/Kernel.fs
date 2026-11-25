namespace Fx.Kernel

open System
open System.Collections.Concurrent
open System.Text
open System.Threading
open System.Threading.Tasks
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Rpc
open Fx.Sdk

/// What the host does with what a kernel asks and reports. The handlers run in the environment of
/// the effect that started the kernel, in the scope of the request (cancelled with it: the kernel
/// cancels a cell's host calls when the cell is cancelled).
type KernelHost<'R> =
    {
        /// Answers a cell's call of a host binding.
        HostCall: HostCall -> Eff<'R, Result<Value, HostCallError>>
        /// Validates a cell's `finish` (codecs and `Ensure`, e.g. with `Finish.validate`).
        Finish: Finish -> Eff<'R, Result<unit, FinishRejected>>
        /// Output as cells produce it. Notification handlers run one at a time, in order: do not
        /// call the kernel from them.
        Output: CellOutput -> Eff<'R, unit>
        Activity: CellActivity -> Eff<'R, unit>
    }

    override _.ToString() = "KernelHost"

module KernelHost =
    /// No bindings, every `finish` rejected, notifications dropped. Override what you need.
    let empty<'R> : KernelHost<'R> =
        {
            HostCall = fun call -> Eff.ret (Error(HostCallError.UnknownBinding call.Binding))
            Finish =
                fun _ ->
                    Eff.ret (
                        Error
                            {
                                Issues = [ Issue.create "this kernel cannot finish the turn" ]
                            }
                    )
            Output = fun _ -> Eff.ret ()
            Activity = fun _ -> Eff.ret ()
        }

/// How to start a kernel process: the process (from a runtime's `LaunchSpec`, resolved by its
/// launcher) and the variables it must never get.
type KernelLaunch =
    {
        Process: ProcSpec
        /// Patterns (a trailing `*` matches any suffix) of variables that must not reach the kernel,
        /// on top of the host's own deny list (`Manifest.deniedEnvironment`): the runtime's injection
        /// vectors (spec §6.2 `EnvDeny`, §18).
        EnvDeny: string list
        /// Compare variable names without case (Windows).
        IgnoreEnvCase: bool
    }

    override this.ToString() = $"launch {this.Process}"

module KernelLaunch =
    /// The launch hygiene hook (spec §18): the environment must be built from scratch, never
    /// inherited from the host, and must not set a denied variable. Returns what is wrong.
    let check (launch: KernelLaunch) : Issue list =
        match launch.Process.Env with
        | Inherit _ ->
            [
                Issue.at
                    [ PathSegment.Key "env" ]
                    "a kernel's environment must be given exactly (EnvVars.Exactly), never inherited from the host"
            ]
        | Exactly vars ->
            let denied = Manifest.deniedEnvironment @ launch.EnvDeny

            vars
            |> List.filter (fun (name, _) ->
                denied
                |> List.exists (fun pattern -> Manifest.matchesEnvPattern launch.IgnoreEnvCase pattern name))
            |> List.map (fun (name, _) ->
                Issue.at [ PathSegment.Key "env"; PathSegment.Key name ] "a runtime injection vector, denied")

/// How the host runs kernels.
type KernelOptions =
    {
        /// The host, as it introduces itself in `initialize`.
        Peer: PeerInfo
        /// The connection's limits (kernels have no binary channels).
        Limits: ConnectionLimits
        /// How much of a kernel's standard error and stray output is kept for diagnostics.
        DiagnosticsBytes: int
        /// How long a closed kernel may take to exit, and an interrupted cell to stop, before the
        /// kernel is killed.
        ExitGrace: TimeSpan
        /// How long `run` waits for the notifications the kernel says it sent before the result.
        NotificationWait: TimeSpan
    }

    override this.ToString() =
        $"KernelOptions({this.Peer.Name}, grace {this.ExitGrace.TotalSeconds} s)"

module KernelOptions =
    let defaults: KernelOptions =
        {
            Peer =
                {
                    Name = Product.binaryName
                    Version = Product.Version
                }
            Limits =
                { ConnectionLimits.defaults with
                    Channels = None
                }
            DiagnosticsBytes = 64 * 1024
            ExitGrace = TimeSpan.FromSeconds 2.0
            NotificationWait = TimeSpan.FromSeconds 5.0
        }

/// Why a kernel did not start.
[<RequireQualifiedAccess>]
type StartError =
    /// The launch failed the hygiene check (`KernelLaunch.check`); nothing was started.
    | UnsafeLaunch of Issue list
    | NotStarted of ShellError
    /// The handshake failed; the end of the kernel's standard error, if any.
    | Handshake of ConnectError * stderr: string
    /// The kernel does not speak a protocol version the host does, or lacks required methods.
    | Incompatible of reason: string

    override this.ToString() =
        match this with
        | UnsafeLaunch issues -> "unsafe launch:\n" + Issue.renderAtMost 10 issues
        | NotStarted e -> string e
        | Handshake(e, "") -> "the kernel did not complete the handshake: " + string e
        | Handshake(e, stderr) ->
            $"the kernel did not complete the handshake: {e}; its standard error ends with: {stderr}"
        | Incompatible reason -> "incompatible kernel: " + reason

/// Why a call to a kernel returned no result.
[<RequireQualifiedAccess>]
type KernelFailure =
    /// The kernel refused with one of the protocol's typed errors.
    | Refused of KernelError
    /// The kernel's connection ended (it exited, crashed or was killed).
    | Exited of CloseReason
    /// No answer in time.
    | Unanswered of after: TimeSpan
    /// The kernel broke the protocol (unknown method, invalid params or reply, an internal error).
    | Broken of reason: string

    override this.ToString() =
        match this with
        | Refused e -> string e
        | Exited reason -> "the kernel exited: " + string reason
        | Unanswered after -> $"no answer after {after.TotalSeconds} s"
        | Broken reason -> "the kernel broke the protocol: " + reason

/// How `Kernel.interrupt` stopped a cell.
[<RequireQualifiedAccess>]
type Interruption =
    /// No cell was running.
    | Idle
    /// The cell stopped within the grace period; the kernel and its state live on.
    | Stopped
    /// The cell did not stop: the kernel was killed. Start a new one; its state is lost (restore it
    /// with `snapshot`/`restore` or `recall` and `value/decode` from the journal).
    | Killed

    override this.ToString() =
        match this with
        | Idle -> "idle"
        | Stopped -> "stopped"
        | Killed -> "killed"

/// Bounded text kept for diagnostics: the last `capacity` bytes.
type internal Tail(capacity: int) =
    let buffer = Collections.Generic.Queue<byte>()
    let gate = obj ()

    member _.Append(bytes: byte array) =
        lock gate (fun () ->
            for b in bytes do
                buffer.Enqueue b

            while buffer.Count > capacity do
                buffer.Dequeue() |> ignore)

    member this.AppendText(text: string) =
        this.Append(Encoding.UTF8.GetBytes text)

    member _.Text() =
        lock gate (fun () -> Encoding.UTF8.GetString(buffer.ToArray()))

/// Counts the notifications handled per cell, so `run` returns after all of a cell's output.
type internal Notified() =
    let counts = ConcurrentDictionary<string, int>(StringComparer.Ordinal)

    let waiters =
        ConcurrentDictionary<string, struct (int * TaskCompletionSource)>(StringComparer.Ordinal)

    member _.Count(cellId: string) =
        match counts.TryGetValue cellId with
        | true, n -> n
        | _ -> 0

    member this.Add(cellId: string) =
        let n = counts.AddOrUpdate(cellId, 1, (fun _ n -> n + 1))

        match waiters.TryGetValue cellId with
        | true, struct (target, signal) when n >= target -> signal.TrySetResult() |> ignore
        | _ -> ()

    /// Completes once `target` notifications of the cell were handled.
    member this.WaitFor(cellId: string, target: int) : Task =
        if this.Count cellId >= target then
            Task.CompletedTask
        else
            let signal =
                TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

            waiters[cellId] <- struct (target, signal)

            if this.Count cellId >= target then
                signal.TrySetResult() |> ignore

            signal.Task

    member _.Forget(cellId: string) = waiters.TryRemove cellId |> ignore

/// A running kernel and the host's connection to it.
[<NoEquality; NoComparison>]
type Kernel =
    internal
        {
            Peer: Peer
            Setup: KernelSetup
            Info: KernelInfo
            Options: KernelOptions
            Methods: string list
            Process: Proc option
            Terminate: unit -> Task<unit>
            Diagnostics: Tail
            Stray: Tail
            Notified: Notified
            /// The cell `run` is waiting for, and its completion.
            Running: (string * TaskCompletionSource) option ref
            Gate: obj
        }

    override this.ToString() =
        match this.Process with
        | Some proc -> $"{this.Info} ({proc})"
        | None -> string this.Info

module Kernel =
    let private failure (error: CallError<KernelError>) : KernelFailure =
        match error with
        | CallError.Failed e -> KernelFailure.Refused e
        | CallError.Disconnected reason -> KernelFailure.Exited reason
        | CallError.TimedOut after -> KernelFailure.Unanswered after
        | CallError.Rejected e -> KernelFailure.Broken(string e)
        | CallError.InvalidReply issues -> KernelFailure.Broken("invalid reply: " + Issue.renderAtMost 5 issues)
        | CallError.TooLarge(size, limit) ->
            KernelFailure.Broken $"the request has {size} bytes, more than the kernel's limit of {limit}"

    /// What the kernel announced in `initialize`.
    let info (kernel: Kernel) : KernelInfo = kernel.Info

    /// What the host told the kernel in `initialize`.
    let setup (kernel: Kernel) : KernelSetup = kernel.Setup

    /// The methods the kernel handles (sorted).
    let methods (kernel: Kernel) : string list = kernel.Methods

    /// Whether the kernel handles an optional method (`cell/check`, `snapshot`, `restore`).
    let supports (kernel: Kernel) (name: string) : bool = List.contains name kernel.Methods

    /// The kernel's process, if it runs in one.
    let proc (kernel: Kernel) : Proc option = kernel.Process

    /// The end of the kernel's standard error, for diagnostics.
    let stderr (kernel: Kernel) : string = kernel.Diagnostics.Text()

    /// The end of what the kernel wrote to standard output outside the protocol.
    let stray (kernel: Kernel) : string = kernel.Stray.Text()

    /// Completes when the connection has ended, with why.
    let exited (kernel: Kernel) : Task<CloseReason> = kernel.Peer.Completion

    let private service<'R> (options: KernelOptions) (host: KernelHost<'R>) (notified: Notified) =
        let counted (cellId: string option) (late: bool) =
            match cellId with
            | Some id when not late -> notified.Add id
            | _ -> ()

        Service.create
            options.Peer
            [
                Handler.request KernelProtocol.hostCall (fun _ call -> host.HostCall call)
                Handler.request KernelProtocol.finish (fun _ finish -> host.Finish finish)
                Handler.notification KernelProtocol.cellOutput (fun _ output ->
                    eff {
                        do! host.Output output
                        counted output.CellId output.Late
                    })
                Handler.notification KernelProtocol.cellActivity (fun _ activity ->
                    eff {
                        do! host.Activity activity
                        counted activity.CellId activity.Late
                    })
            ]
        |> Service.withLimits options.Limits

    let private handshake (setup: KernelSetup) (peer: Peer) : Result<KernelInfo * string list, string> =
        let remote = peer.Remote

        let kernelMember =
            match remote.Members with
            | Json.Object members ->
                members
                |> List.tryFind (fun (name, _) -> name = KernelProtocol.InitializeMember)
                |> Option.map snd
            | _ -> None

        match kernelMember with
        | None -> Error $"the initialize result has no {Json.quote KernelProtocol.InitializeMember} member"
        | Some json ->
            match KernelProtocol.Codecs.kernelInfo.Decode json with
            | Error issues -> Error("invalid kernel info:\n" + Issue.renderAtMost 5 issues)
            | Ok info when not (List.contains info.Version setup.Versions) ->
                Error $"the kernel chose protocol {Json.quote info.Version}, which the host did not offer"
            | Ok info ->
                let methods = remote.Capabilities.Methods

                match
                    KernelProtocol.requiredMethods
                    |> List.filter (fun m -> not (List.contains m methods))
                with
                | [] -> Ok(info, methods)
                | missing -> Error("the kernel does not handle " + String.Join(", ", missing))

    let private connectWith<'R when 'R :> IClock and 'R :> IScopeWith<'R>>
        (options: KernelOptions)
        (host: KernelHost<'R>)
        (setup: KernelSetup)
        (proc: Proc option)
        (terminate: unit -> Task<unit>)
        (stderrTail: Tail)
        (strayTail: Tail)
        (transport: Transport)
        : Eff<'R, Result<Kernel, StartError>> =
        eff {
            let notified = Notified()

            let members =
                [
                    KernelProtocol.InitializeMember, KernelProtocol.Codecs.kernelSetup.Encode setup
                ]

            match! Rpc.connect (service options host notified) members transport with
            | Error e -> return Error(StartError.Handshake(e, stderrTail.Text().Trim()))
            | Ok peer ->
                match handshake setup peer with
                | Error reason ->
                    let! _ = Rpc.close peer
                    return Error(StartError.Incompatible reason)
                | Ok(info, methods) ->
                    return
                        Ok
                            {
                                Peer = peer
                                Setup = setup
                                Info = info
                                Options = options
                                Methods = methods
                                Process = proc
                                Terminate = terminate
                                Diagnostics = stderrTail
                                Stray = strayTail
                                Notified = notified
                                Running = ref None
                                Gate = obj ()
                            }
        }

    /// Connects to a kernel that is not a process of ours (in process, or reached otherwise).
    /// `terminate` ends it abruptly (`interrupt` uses it when a cell does not stop).
    let connect<'R when 'R :> IClock and 'R :> IScopeWith<'R>>
        (options: KernelOptions)
        (host: KernelHost<'R>)
        (setup: KernelSetup)
        (terminate: unit -> Task<unit>)
        (transport: Transport)
        : Eff<'R, Result<Kernel, StartError>> =
        connectWith
            options
            host
            setup
            None
            terminate
            (Tail options.DiagnosticsBytes)
            (Tail options.DiagnosticsBytes)
            transport

    /// Starts a kernel process with the shell capability and connects to it. The launch must pass
    /// `KernelLaunch.check` first. If the handshake fails, the process is killed.
    let start<'R when 'R :> IShell and 'R :> IClock and 'R :> IScopeWith<'R>>
        (options: KernelOptions)
        (host: KernelHost<'R>)
        (setup: KernelSetup)
        (launch: KernelLaunch)
        : Eff<'R, Result<Kernel, StartError>> =
        eff {
            match KernelLaunch.check launch with
            | _ :: _ as issues -> return Error(StartError.UnsafeLaunch issues)
            | [] ->
                match! Shell.spawn launch.Process with
                | Error e -> return Error(StartError.NotStarted e)
                | Ok proc ->
                    let stderrTail = Tail options.DiagnosticsBytes
                    let strayTail = Tail options.DiagnosticsBytes

                    let transport =
                        KernelTransport.create
                            (ConnectionLimits.frames options.Limits)
                            proc
                            strayTail.AppendText
                            stderrTail.Append

                    let! connected =
                        connectWith options host setup (Some proc) proc.Kill stderrTail strayTail transport

                    match connected with
                    | Ok kernel -> return Ok kernel
                    | Error e ->
                        do! Shell.kill proc
                        let! _ = Eff.shield (Shell.wait proc)
                        return Error e
        }

    let private call
        (kernel: Kernel)
        (m: Method<'P, 'T, KernelError>)
        (value: 'P)
        : Eff<'R, Result<'T, KernelFailure>> when 'R :> IScope =
        fun env ->
            task {
                let! outcome = Rpc.call kernel.Peer m value env
                return outcome |> Result.mapError failure
            }

    let private waitNotified<'R when 'R :> IClock and 'R :> IScopeWith<'R>>
        (kernel: Kernel)
        (result: CellResult)
        : Eff<'R, unit> =
        eff {
            let waiting = kernel.Notified.WaitFor(result.CellId, result.Notified)

            let arrived (env: 'R) =
                task {
                    let! _ =
                        Task.WhenAny(waiting, kernel.Peer.Completion).WaitAsync(env.Scope.Cancellation)

                    return ()
                }

            let! _ = Eff.timeout kernel.Options.NotificationWait arrived
            kernel.Notified.Forget result.CellId
        }

    /// Runs a cell and returns its result once the host has handled the output and activity the
    /// kernel sent before it. Cancelling the scope cancels the request, which interrupts the cell.
    let run<'R when 'R :> IClock and 'R :> IScopeWith<'R>>
        (kernel: Kernel)
        (cell: CellRun)
        : Eff<'R, Result<CellResult, KernelFailure>> =
        eff {
            let signal =
                TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

            lock kernel.Gate (fun () -> kernel.Running.Value <- Some(cell.CellId, signal))

            try
                let! outcome = call kernel KernelProtocol.cellRun cell

                match outcome with
                | Ok result ->
                    do! waitNotified kernel result
                    return Ok result
                | Error e -> return Error e
            finally
                lock kernel.Gate (fun () ->
                    match kernel.Running.Value with
                    | Some(id, _) when id = cell.CellId -> kernel.Running.Value <- None
                    | _ -> ())

                signal.TrySetResult() |> ignore
        }

    /// Asks the kernel to interrupt a cell (`cell/cancel`).
    let cancel (kernel: Kernel) (cellId: string) : Eff<'R, Result<Cancelled, KernelFailure>> when 'R :> IScope =
        call kernel KernelProtocol.cellCancel { CellId = cellId }

    /// Kills the kernel (its process, or what `connect` was given). Calls in flight end `Exited`.
    let kill (kernel: Kernel) : Eff<'R, unit> =
        fun _ ->
            task {
                do! kernel.Terminate()
                kernel.Peer.CloseAsync() |> ignore
            }

    /// Stops the running cell in stages (docs/modules/kernel-protocol.md, "Cancellation"):
    /// `cell/cancel`, then, if the cell has not stopped within `ExitGrace` (a busy loop the kernel
    /// cannot interrupt), a kill. A killed kernel's state is lost.
    let interrupt<'R when 'R :> IClock and 'R :> IScopeWith<'R>> (kernel: Kernel) : Eff<'R, Interruption> =
        eff {
            match lock kernel.Gate (fun () -> kernel.Running.Value) with
            | None -> return Interruption.Idle
            | Some(cellId, signal) ->
                let stopped (env: 'R) =
                    task {
                        do! signal.Task.WaitAsync(env.Scope.Cancellation)
                        return ()
                    }

                let! outcome =
                    Eff.timeout
                        kernel.Options.ExitGrace
                        (eff {
                            let! _ = Eff.catch (cancel kernel cellId)
                            do! stopped
                        })

                match outcome with
                | Some() -> return Interruption.Stopped
                | None ->
                    do! kill kernel
                    return Interruption.Killed
        }

    /// Type-checks a cell, if the kernel handles `cell/check`.
    let check (kernel: Kernel) (cell: CellCheck) : Eff<'R, Result<Checked, KernelFailure>> when 'R :> IScope =
        call kernel KernelProtocol.cellCheck cell

    /// A preview of a value the kernel holds, within the kernel's limits or the ones given.
    let preview
        (kernel: Kernel)
        (ref: ValueRef)
        (limits: Limits option)
        : Eff<'R, Result<ValueInfo, KernelFailure>> when 'R :> IScope =
        call kernel KernelProtocol.valuePreview { Ref = ref; Limits = limits }

    /// A value the kernel holds, encoded (inline up to `maxBytes`, or the kernel's `InlineBytes`).
    let encode
        (kernel: Kernel)
        (ref: ValueRef)
        (maxBytes: int option)
        : Eff<'R, Result<Value, KernelFailure>> when 'R :> IScope =
        call kernel KernelProtocol.valueEncode { Ref = ref; MaxBytes = maxBytes }

    /// A value the kernel holds, decoded into a declared type: `Error` with issues if it does not
    /// fit (spec §6.6), or if the kernel could not encode it.
    let encodeAs
        (kernel: Kernel)
        (codec: Codec<'T>)
        (ref: ValueRef)
        : Eff<'R, Result<'T, Choice<KernelFailure, Issue list>>> when 'R :> IScope =
        eff {
            match! encode kernel ref None with
            | Error e -> return Error(Choice1Of2 e)
            | Ok value ->
                match Value.decode codec value with
                | Ok decoded -> return Ok decoded
                | Error issues -> return Error(Choice2Of2 issues)
        }

    /// Binds a value to a top-level variable of the kernel.
    let decode
        (kernel: Kernel)
        (name: string)
        (value: Value)
        (typeName: string option)
        : Eff<'R, Result<ValueInfo, KernelFailure>> when 'R :> IScope =
        call
            kernel
            KernelProtocol.valueDecode
            {
                Name = name
                Value = value
                Type = typeName
            }

    /// Restores a cell's completion value (`Out[cellId]`).
    let recall
        (kernel: Kernel)
        (cellId: string)
        (value: Value)
        : Eff<'R, Result<ValueInfo, KernelFailure>> when 'R :> IScope =
        call kernel KernelProtocol.recall { CellId = cellId; Value = value }

    /// The kernel's state, if it supports snapshots.
    let snapshot (kernel: Kernel) : Eff<'R, Result<KernelState, KernelFailure>> when 'R :> IScope =
        call kernel KernelProtocol.snapshot ()

    /// Replaces the kernel's state with a snapshot's.
    let restore (kernel: Kernel) (state: KernelState) : Eff<'R, Result<unit, KernelFailure>> when 'R :> IScope =
        call kernel KernelProtocol.restore state

    /// Shuts the kernel down: closes the connection (the kernel's input ends, and it exits) and waits
    /// for its process to exit, killing it after `ExitGrace`. Returns the exit code of a process.
    let close<'R when 'R :> IClock and 'R :> IScopeWith<'R>> (kernel: Kernel) : Eff<'R, int option> =
        eff {
            let! _ = Rpc.close kernel.Peer

            match kernel.Process with
            | None -> return None
            | Some proc ->
                let! exited = Eff.timeout kernel.Options.ExitGrace (Shell.wait proc)

                match exited with
                | Some code -> return Some code
                | None ->
                    do! Shell.kill proc
                    let! code = Eff.shield (Shell.wait proc)
                    return Some code
        }
