/// Pseudo-consoles (ConPTY) on Windows 10 1809+ and Windows 11, x64 and arm64.
///
/// 1. Two anonymous pipes; `CreatePseudoConsole` gets their console ends, which are then closed
///    here (ConPTY keeps its own copies), so the output pipe ends exactly when the console closes.
/// 2. A job object that kills its processes when its last handle closes.
/// 3. .NET builds the command line and environment block (`WindowsProcessStartArguments.Start`),
///    and the callback calls `CreateProcessW` with an attribute list holding the pseudo-console and
///    the job, `STARTF_USESTDHANDLES` with null handles (otherwise a child can write to this
///    process's redirected standard handles instead of the console) and no inherited handles.
/// 4. A process exit does not end the output: ConPTY keeps the console open. Once the program
///    exits, `ClosePseudoConsole` flushes the last frame and ends the output pipe. It can block until
///    the output is drained, which the output thread does.
module internal Fx.Pty.WindowsPty

#nowarn "9" "51" // Pointers: the CreateProcess callback works on native strings and out parameters.

open System
open System.ComponentModel
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Threading.Tasks
open Microsoft.Win32.SafeHandles
open Microsoft.FSharp.NativeInterop
open Fx.Pty.Native

let private withHandle (handle: SafeFileHandle) (f: nativeint -> 'T) : 'T option =
    let mutable added = false

    try
        try
            handle.DangerousAddRef(&added)
            Some(f (handle.DangerousGetHandle()))
        with :? ObjectDisposedException ->
            None
    finally
        if added then
            handle.DangerousRelease()

let private createPipe () : Result<SafeFileHandle * SafeFileHandle, string> =
    let mutable read = 0n
    let mutable write = 0n

    if Windows.CreatePipe(NativePtr.toNativeInt &&read, NativePtr.toNativeInt &&write, 0n, 0u) = 0 then
        Error $"CreatePipe: {Windows.describe (Windows.lastError ())}"
    else
        Ok(new SafeFileHandle(read, true), new SafeFileHandle(write, true))

/// The pseudo-console handle: resized and closed under one lock, closed exactly once.
[<Sealed>]
type private Console(handle: nativeint) =
    let gate = obj ()
    let mutable current = handle

    /// The handle, for creating the process (before anything can close it).
    member _.Handle = current

    member _.Resize(size: PtySize) =
        lock gate (fun () ->
            if current <> 0n then
                Windows.ResizePseudoConsole(current, Windows.coord size.Cols size.Rows)
                |> ignore)

    member _.Close() =
        lock gate (fun () ->
            if current <> 0n then
                Windows.ClosePseudoConsole current
                current <- 0n)

let private createJob () : Result<nativeint, string> =
    let job = Windows.CreateJobObject(0n, 0n)

    if job = 0n then
        Error $"CreateJobObject: {Windows.describe (Windows.lastError ())}"
    else
        let info = Windows.alloc Windows.ExtendedLimitInformation.Size

        try
            Marshal.WriteInt32(info, Windows.ExtendedLimitInformation.LimitFlags, Windows.JobObjectLimitKillOnJobClose)

            if
                Windows.SetInformationJobObject(
                    job,
                    Windows.JobObjectExtendedLimitInformation,
                    info,
                    uint32 Windows.ExtendedLimitInformation.Size
                )
                    =
                    0
            then
                let error = Windows.lastError ()
                Windows.CloseHandle job |> ignore
                Error $"SetInformationJobObject: {Windows.describe error}"
            else
                Ok job
        finally
            Windows.free info

/// Creates the process on the pseudo-console, in the job. Runs inside .NET's process-start lock;
/// returns the process handle and throws on failure.
let private createProcess
    (console: nativeint)
    (job: nativeint)
    (cwd: string)
    (commandLine: nativeint)
    (environment: nativeint)
    : nativeint =
    let mutable size = 0n
    // The first call only reports the size of a list with two attributes.
    Windows.InitializeProcThreadAttributeList(0n, 2, 0, NativePtr.toNativeInt &&size)
    |> ignore

    let list = Windows.alloc (int size)
    let jobs = Windows.alloc IntPtr.Size
    let startup = Windows.alloc Windows.StartupInfoEx.Size
    let info = Windows.alloc Windows.ProcessInformation.Size

    let directory =
        if String.IsNullOrEmpty cwd then
            0n
        else
            Marshal.StringToHGlobalUni cwd

    let mutable listReady = false

    let fail (what: string) =
        let error = Windows.lastError ()
        raise (Win32Exception(error, $"{what}: {Windows.describe error}"))

    try
        if Windows.InitializeProcThreadAttributeList(list, 2, 0, NativePtr.toNativeInt &&size) = 0 then
            fail "InitializeProcThreadAttributeList"

        listReady <- true

        // The pseudo-console attribute takes the handle itself as its value.
        if
            Windows.UpdateProcThreadAttribute(
                list,
                0u,
                Windows.ProcThreadAttributePseudoConsole,
                console,
                nativeint IntPtr.Size,
                0n,
                0n
            )
                =
                0
        then
            fail "UpdateProcThreadAttribute(PSEUDOCONSOLE)"

        // The job list attribute takes a pointer to an array of handles, which must outlive the list.
        Marshal.WriteIntPtr(jobs, 0, job)

        if
            Windows.UpdateProcThreadAttribute(
                list,
                0u,
                Windows.ProcThreadAttributeJobList,
                jobs,
                nativeint IntPtr.Size,
                0n,
                0n
            )
                =
                0
        then
            fail "UpdateProcThreadAttribute(JOB_LIST)"

        Marshal.WriteInt32(startup, 0, Windows.StartupInfoEx.Size)
        Marshal.WriteInt32(startup, Windows.StartupInfoEx.Flags, Windows.StartfUseStdHandles)
        Marshal.WriteIntPtr(startup, Windows.StartupInfoEx.AttributeList, list)

        let flags = Windows.ExtendedStartupInfoPresent ||| Windows.CreateUnicodeEnvironment

        if Windows.CreateProcess(0n, commandLine, 0n, 0n, 0, flags, environment, directory, startup, info) = 0 then
            fail "CreateProcess"

        Windows.CloseHandle(Marshal.ReadIntPtr(info, Windows.ProcessInformation.Thread))
        |> ignore

        Marshal.ReadIntPtr(info, Windows.ProcessInformation.Process)
    finally
        if listReady then
            Windows.DeleteProcThreadAttributeList list

        if directory <> 0n then
            Marshal.FreeHGlobal directory

        Windows.free info
        Windows.free startup
        Windows.free jobs
        Windows.free list

let private io (input: SafeFileHandle) (output: SafeFileHandle) : PtyIo =
    {
        Read =
            fun buffer length ->
                withHandle output (fun handle ->
                    let mutable result = -1

                    while result < 0 do
                        let mutable count = 0u

                        if
                            Windows.ReadFile(handle, buffer, uint32 length, NativePtr.toNativeInt &&count, 0n)
                            <> 0
                        then
                            // A zero-byte read is not the end of a pipe; only a broken pipe is.
                            if count > 0u then
                                result <- int count
                        else
                            match Windows.lastError () with
                            | e when e = Windows.ErrorBrokenPipe -> result <- 0
                            | e -> raise (IOException($"ReadFile: {Windows.describe e}", e))

                    result)
                |> Option.defaultValue 0
        Write =
            fun buffer length ->
                withHandle input (fun handle ->
                    let mutable offset = 0
                    let mutable writing = true

                    while writing && offset < length do
                        let mutable count = 0u

                        if
                            Windows.WriteFile(
                                handle,
                                buffer + nativeint offset,
                                uint32 (length - offset),
                                NativePtr.toNativeInt &&count,
                                0n
                            )
                            <> 0
                        then
                            offset <- offset + int count
                        else
                            // ERROR_BROKEN_PIPE or ERROR_NO_DATA: the console has closed.
                            writing <- false

                    writing)
                |> Option.defaultValue false
        Release =
            fun () ->
                input.Dispose()
                output.Dispose()
    }

/// The exit code of a program that `Kill` ended, as `Process.Kill` gives on Windows.
let KilledExitCode = -1

let private ofProcess
    (proc: Process)
    (console: Console)
    (job: nativeint)
    (input: SafeFileHandle)
    (output: SafeFileHandle)
    : Pty =
    let pid = proc.Id

    let exited =
        task {
            do! proc.WaitForExitAsync()
            return proc.ExitCode
        }

    let streams = new PtyStreams($"conpty {pid}", io input output)
    // Kill and the clean-up below never overlap: the job handle is closed there.
    let gate = obj ()
    let mutable closed = false

    task {
        let! _ = exited
        // Flushes the last frame and ends the output; the output thread drains it meanwhile.
        console.Close()

        lock gate (fun () ->
            closed <- true
            // Kills what is left in the job (e.g. a program the shell started in the background).
            Windows.CloseHandle job |> ignore
            proc.Dispose())
    }
    |> ignore

    let killIfRunning () =
        lock gate (fun () ->
            if not closed && not exited.IsCompleted then
                Windows.TerminateJobObject(job, uint32 KilledExitCode) |> ignore)

    {
        Pid = pid
        Write = streams.Write
        Read = streams.Read
        Resize =
            fun size ->
                console.Resize size
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

/// Starts the process described by `info` on a new pseudo-console of the given size.
let spawn (info: ProcessStartInfo) (size: PtySize) : Result<Pty, PtyError> =
    let notStarted (reason: string) =
        Error(NotStarted(info.FileName, reason))

    match createPipe (), createPipe () with
    | Error reason, other
    | other, Error reason ->
        match other with
        | Ok(a, b) ->
            a.Dispose()
            b.Dispose()
        | Error _ -> ()

        notStarted reason
    | Ok(consoleInput, input), Ok(output, consoleOutput) ->
        let mutable console = 0n

        let created =
            Windows.CreatePseudoConsole(
                Windows.coord size.Cols size.Rows,
                consoleInput.DangerousGetHandle(),
                consoleOutput.DangerousGetHandle(),
                0u,
                NativePtr.toNativeInt &&console
            )

        // ConPTY duplicated its ends; closing ours lets the output end when the console closes.
        consoleInput.Dispose()
        consoleOutput.Dispose()

        if created < 0 then
            input.Dispose()
            output.Dispose()
            notStarted $"CreatePseudoConsole: 0x{created:x8}"
        else
            let console = Console(console)

            match createJob () with
            | Error reason ->
                console.Close()
                input.Dispose()
                output.Dispose()
                notStarted reason
            | Ok job ->
                try
                    let cwd = info.WorkingDirectory

                    let callback =
                        Func<WindowsProcessStartArguments, nativeint>(fun args ->
                            createProcess
                                console.Handle
                                job
                                cwd
                                (NativePtr.toNativeInt args.Arguments)
                                (NativePtr.toNativeInt args.EnvironmentVariables))

                    let proc = WindowsProcessStartArguments.Start(info, callback)
                    Ok(ofProcess proc console job input output)
                with :? Win32Exception as e ->
                    console.Close()
                    Windows.CloseHandle job |> ignore
                    input.Dispose()
                    output.Dispose()
                    notStarted e.Message
