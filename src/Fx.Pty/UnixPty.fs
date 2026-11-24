/// Pseudo-terminals on Linux (glibc and musl) and macOS.
///
/// No managed code ever runs in a forked child: `forkpty` followed by managed code is unsafe in a
/// multithreaded runtime (only async-signal-safe calls are allowed between `fork` and `exec`). Instead:
///
/// 1. The parent opens the master with `open("/dev/ptmx", O_RDWR | O_NOCTTY | O_CLOEXEC)`, then
///    `grantpt`, `unlockpt` and `ptsname_r`. It opens the slave too (`O_NOCTTY`, so it never
///    becomes the controlling process) to set the size, and closes it as soon as the child has it,
///    so that the output ends when the child's side closes.
/// 2. .NET resolves the program and builds `argv` and `envp`
///    (`UnixProcessStartArguments.Start`, which also registers the child for reaping), and the
///    callback creates the child with `posix_spawn`: a new session (`POSIX_SPAWN_SETSID`), every
///    signal at its default action, an empty signal mask, and file actions that open the slave as
///    descriptor 0, duplicate it to 1 and 2, and change the directory.
/// 3. Linux (glibc and musl) runs `setsid` before the file actions, so opening the slave without
///    `O_NOCTTY` makes it the controlling terminal of the new session. macOS (XNU) runs the file
///    actions first, so there `/bin/sh` reopens the slave after `setsid` and then `exec`s the
///    program.
module internal Fx.Pty.UnixPty

#nowarn "9" "51" // Pointers: the posix_spawn callback works on native argv and envp arrays.

open System
open System.ComponentModel
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Threading.Tasks
open Microsoft.Win32.SafeHandles
open Microsoft.FSharp.NativeInterop
open Fx.Pty.Native

let private c = Posix.constants

/// Runs `f` with the descriptor of a handle that might be closed concurrently; `None` once closed.
let private withFd (handle: SafeFileHandle) (f: int -> 'T) : 'T option =
    let mutable added = false

    try
        try
            handle.DangerousAddRef(&added)
            Some(f (int (handle.DangerousGetHandle())))
        with :? ObjectDisposedException ->
            None
    finally
        if added then
            handle.DangerousRelease()

/// Sets the size on either side of the terminal.
let private setSizeOn (fd: int) (size: PtySize) =
    let mutable ws: Posix.WinSize =
        {
            Rows = uint16 size.Rows
            Cols = uint16 size.Cols
            XPixel = 0us
            YPixel = 0us
        }

    if Posix.ioctl fd c.SetWinSize (NativePtr.toNativeInt &&ws) <> 0 then
        let error = Posix.errno ()
        raise (Win32Exception(error, $"TIOCSWINSZ: {Posix.describe error}"))

let private setSize (master: SafeFileHandle) (size: PtySize) =
    withFd master (fun fd -> setSizeOn fd size) |> ignore

/// Opens the slave in this process, without making it the controlling terminal. macOS refuses
/// terminal ioctls on the master until the slave has been opened once, and resets the size when the
/// slave is opened while no one has it open: so the slave stays open here until the child has it.
let private openSlave (slave: string) : Result<SafeFileHandle, string> =
    let path = Marshal.StringToCoTaskMemUTF8 slave

    try
        let fd = Posix.openNoMode (path, Posix.ORdWr ||| c.NoCtty ||| c.CloExec)

        if fd < 0 then
            Error $"open {slave}: {Posix.describe (Posix.errno ())}"
        else
            Ok(new SafeFileHandle(nativeint fd, true))
    finally
        Marshal.FreeCoTaskMem path

/// Opens a new master and returns it with the path of its slave.
let private openMaster () : Result<SafeFileHandle * string, string> =
    let path = Marshal.StringToCoTaskMemUTF8 "/dev/ptmx"

    let fd, error =
        try
            let fd = Posix.openNoMode (path, Posix.ORdWr ||| c.NoCtty ||| c.CloExec)
            fd, (if fd < 0 then Posix.errno () else 0)
        finally
            Marshal.FreeCoTaskMem path

    if fd < 0 then
        Error $"open /dev/ptmx: {Posix.describe error}"
    else
        let master = new SafeFileHandle(nativeint fd, true)
        let name = Posix.alloc 256un

        try
            if Posix.grantpt fd <> 0 then
                master.Dispose()
                Error $"grantpt: {Posix.describe (Posix.errno ())}"
            elif Posix.unlockpt fd <> 0 then
                master.Dispose()
                Error $"unlockpt: {Posix.describe (Posix.errno ())}"
            else
                match Posix.ptsname_r (fd, name, 255un) with
                | 0 ->
                    match Marshal.PtrToStringUTF8 name with
                    | null -> Error "ptsname_r returned no name"
                    | slave -> Ok(master, slave)
                | result ->
                    master.Dispose()
                    let error = if result > 0 then result else Posix.errno ()
                    Error $"ptsname_r: {Posix.describe error}"
        finally
            Posix.free name

/// What runs `exec` on macOS once the slave is the controlling terminal: `$1` is the slave, the rest
/// is the program and its arguments. Opening a terminal without `O_NOCTTY` as a session leader that
/// has none makes it the controlling terminal (XNU `open1`).
let private macOSLauncher = "exec 0<>\"$1\" || exit 127; shift; exec \"$@\""

/// The null-terminated pointers of a native `argv` array.
let private pointers (array: nativeint) : nativeint list =
    let mutable items = []
    let mutable i = 0
    let mutable item = Marshal.ReadIntPtr(array, 0)

    while item <> 0n do
        items <- item :: items
        i <- i + 1
        item <- Marshal.ReadIntPtr(array, i * IntPtr.Size)

    List.rev items

/// Creates the child with `posix_spawn`. Runs inside .NET's process-start lock; throws on failure.
let private spawnChild (slave: string) (cwd: string) (path: nativeint) (argv: nativeint) (envp: nativeint) : int =
    let check (what: string) (result: int) =
        if result <> 0 then
            raise (Win32Exception(result, $"{what}: {Posix.describe result}"))

    let macOS = OperatingSystem.IsMacOS()
    let attr = Posix.alloc Posix.OpaqueSize
    let actions = Posix.alloc Posix.OpaqueSize
    let defaults = Posix.alloc Posix.SigSetSize
    let mask = Posix.alloc Posix.SigSetSize
    let strings = ResizeArray<nativeint>()

    let utf8 (text: string) =
        let p = Marshal.StringToCoTaskMemUTF8 text
        strings.Add p
        p

    let mutable attrReady = false
    let mutable actionsReady = false
    let mutable launcherArgv = 0n

    try
        check "posix_spawnattr_init" (Posix.posix_spawnattr_init attr)
        attrReady <- true
        check "posix_spawn_file_actions_init" (Posix.posix_spawn_file_actions_init actions)
        actionsReady <- true

        // Every signal at its default action (the runtime ignores or handles some, e.g. SIGPIPE),
        // and none blocked: what a program expects from a fresh terminal session.
        Posix.sigfillset defaults |> ignore
        Posix.sigdelset (defaults, Posix.SigKill) |> ignore
        Posix.sigdelset (defaults, c.SigStop) |> ignore
        Posix.sigemptyset mask |> ignore

        let flags =
            c.SpawnSetSid
            ||| Posix.SpawnSetSigDef
            ||| Posix.SpawnSetSigMask
            ||| c.SpawnCloExecDefault

        check "posix_spawnattr_setflags" (Posix.posix_spawnattr_setflags (attr, flags))
        check "posix_spawnattr_setsigdefault" (Posix.posix_spawnattr_setsigdefault (attr, defaults))
        check "posix_spawnattr_setsigmask" (Posix.posix_spawnattr_setsigmask (attr, mask))

        // On Linux this open makes the slave the controlling terminal; on macOS the launcher does.
        let openFlags = if macOS then Posix.ORdWr ||| c.NoCtty else Posix.ORdWr
        let slavePath = utf8 slave
        check "addopen" (Posix.posix_spawn_file_actions_addopen (actions, 0, slavePath, openFlags, 0u))
        check "adddup2" (Posix.posix_spawn_file_actions_adddup2 (actions, 0, 1))
        check "adddup2" (Posix.posix_spawn_file_actions_adddup2 (actions, 0, 2))

        if not (String.IsNullOrEmpty cwd) then
            check "addchdir" (Posix.posix_spawn_file_actions_addchdir_np (actions, utf8 cwd))

        let program, programArgv =
            if macOS then
                let launcher =
                    [ utf8 "/bin/sh"; utf8 "-c"; utf8 macOSLauncher; utf8 "sh"; slavePath; path ]
                    @ List.tail (pointers argv)

                launcherArgv <- Posix.alloc (unativeint ((launcher.Length + 1) * IntPtr.Size))

                launcher
                |> List.iteri (fun i p -> Marshal.WriteIntPtr(launcherArgv, i * IntPtr.Size, p))

                launcher.Head, launcherArgv
            else
                path, argv

        let mutable pid = 0
        check "posix_spawn" (Posix.posix_spawn (NativePtr.toNativeInt &&pid, program, actions, attr, programArgv, envp))
        pid
    finally
        if actionsReady then
            Posix.posix_spawn_file_actions_destroy actions |> ignore

        if attrReady then
            Posix.posix_spawnattr_destroy attr |> ignore

        for p in strings do
            Marshal.FreeCoTaskMem p

        Posix.free launcherArgv
        Posix.free mask
        Posix.free defaults
        Posix.free actions
        Posix.free attr

let private io (master: SafeFileHandle) : PtyIo =
    {
        Read =
            fun buffer length ->
                withFd master (fun fd ->
                    let mutable result = -1

                    while result < 0 do
                        let count = Posix.read (fd, buffer, unativeint length)

                        if count >= 0n then
                            // 0: end of file (macOS).
                            result <- int count
                        else
                            match Posix.errno () with
                            | e when e = Posix.EIntr -> ()
                            // Linux: every descriptor of the slave is closed.
                            | e when e = Posix.EIo -> result <- 0
                            | e -> raise (IOException($"read: {Posix.describe e}", e))

                    result)
                |> Option.defaultValue 0
        Write =
            fun buffer length ->
                withFd master (fun fd ->
                    let mutable offset = 0
                    let mutable writing = true

                    while writing && offset < length do
                        let count =
                            Posix.write (fd, buffer + nativeint offset, unativeint (length - offset))

                        if count >= 0n then
                            offset <- offset + int count
                        elif Posix.errno () <> Posix.EIntr then
                            writing <- false

                    writing)
                |> Option.defaultValue false
        Release = fun () -> master.Dispose()
    }

let private kill (proc: Process) (pid: int) =
    // The tree first, while parents still lead to their children; then the session's process group,
    // which also reaches processes whose parent already exited.
    try
        proc.Kill(entireProcessTree = true)
    with
    // The process can exit meanwhile (an AggregateException collects the failures in the tree).
    | :? AggregateException
    | :? InvalidOperationException
    | :? Win32Exception
    | :? NotSupportedException -> ()

    Posix.kill (-pid, Posix.SigKill) |> ignore

let private ofProcess (proc: Process) (master: SafeFileHandle) : Pty =
    let pid = proc.Id

    let exited =
        task {
            do! proc.WaitForExitAsync()
            return proc.ExitCode
        }

    let streams = new PtyStreams($"pty {pid}", io master)
    // Kill and the disposal below never overlap: once the process is reaped its id can be reused.
    let gate = obj ()
    let mutable disposed = false

    task {
        let! _ = exited

        lock gate (fun () ->
            disposed <- true
            proc.Dispose())
    }
    |> ignore

    let killIfRunning () =
        lock gate (fun () ->
            if not disposed && not exited.IsCompleted then
                kill proc pid)

    {
        Pid = pid
        Write = streams.Write
        Read = streams.Read
        Resize =
            fun size ->
                setSize master size
                Task.FromResult()
        Wait = fun cancellation -> exited.WaitAsync cancellation
        Kill =
            fun () ->
                killIfRunning ()
                Task.FromResult()
        Close =
            fun () ->
                killIfRunning ()
                streams.Discard()
                Task.FromResult()
    }

/// Starts the process described by `info` on a new terminal of the given size.
let spawn (info: ProcessStartInfo) (size: PtySize) : Result<Pty, PtyError> =
    let notStarted (reason: string) =
        Error(NotStarted(info.FileName, reason))

    match openMaster () with
    | Error reason -> notStarted reason
    | Ok(master, slave) ->
        match openSlave slave with
        | Error reason ->
            master.Dispose()
            notStarted reason
        | Ok slaveHandle ->
            use _ = slaveHandle

            try
                setSizeOn (int (slaveHandle.DangerousGetHandle())) size
                // A new session with the null device as the handles .NET prepares; the callback ignores
                // them. .NET then never treats the child as a user of its own terminal.
                info.StartDetached <- true
                let cwd = info.WorkingDirectory

                let callback =
                    Func<UnixProcessStartArguments, int>(fun args ->
                        spawnChild
                            slave
                            cwd
                            (NativePtr.toNativeInt args.ResolvedPath)
                            (NativePtr.toNativeInt args.Arguments)
                            (NativePtr.toNativeInt args.EnvironmentVariables))

                let proc = UnixProcessStartArguments.Start(info, callback)
                Ok(ofProcess proc master)
            with
            | :? Win32Exception as e ->
                master.Dispose()
                notStarted e.Message
            | :? IOException as e ->
                master.Dispose()
                notStarted e.Message
