namespace Fx.Tui.Live

#nowarn "9" // Native memory: this is the terminal's interop layer.

open System
open System.Runtime.InteropServices
open System.Text
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Fx.Tui
open Fx.Tui.Live.Native

/// What one platform's terminal does: input in pieces (waiting briefly; `None` when none came,
/// `Some ""` at the end of input), output, size, and giving the modes back.
[<NoEquality; NoComparison>]
type private Backend =
    {
        Read: unit -> string option
        Write: string -> unit
        Size: unit -> TtySize
        Restore: unit -> unit
    }

module private Unix =
    let private fallback = { Columns = 80; Rows = 24 }

    let isTerminal () =
        Posix.isatty 0 = 1 && Posix.isatty 1 = 1

    let private writeAll (text: string) =
        let bytes = Encoding.UTF8.GetBytes text
        let handle = GCHandle.Alloc(bytes, GCHandleType.Pinned)

        try
            let start = handle.AddrOfPinnedObject()
            let mutable offset = 0
            let mutable failed = false

            while not failed && offset < bytes.Length do
                let n =
                    Posix.write (1, start + nativeint offset, unativeint (bytes.Length - offset))
                    |> int

                if n > 0 then
                    offset <- offset + n
                else
                    let error = Posix.errno ()

                    if error <> Posix.EIntr && error <> Posix.EAgain then
                        failed <- true
        finally
            handle.Free()

    let private size () =
        let ws = Posix.alloc 8

        try
            if Posix.ioctl 1 Posix.GetWinSize ws = 0 || Posix.ioctl 0 Posix.GetWinSize ws = 0 then
                let rows = int (Marshal.ReadInt16(ws, 0) |> uint16)
                let columns = int (Marshal.ReadInt16(ws, 2) |> uint16)

                if rows > 0 && columns > 0 then
                    { Columns = columns; Rows = rows }
                else
                    fallback
            else
                fallback
        finally
            Posix.free ws

    let openRaw () : Result<Backend, string> =
        if not (isTerminal ()) then
            Error "standard input and output are not a terminal"
        else
            let saved = Posix.alloc Posix.TermiosSize

            if Posix.tcgetattr (0, saved) <> 0 then
                Posix.free saved
                Error $"the terminal's modes could not be read (errno {Posix.errno ()})"
            else
                let raw = Posix.alloc Posix.TermiosSize

                for i in 0 .. Posix.TermiosSize - 1 do
                    Marshal.WriteByte(raw, i, Marshal.ReadByte(saved, i))

                Posix.cfmakeraw raw
                let set = Posix.tcsetattr (0, Posix.TcsaNow, raw)
                Posix.free raw

                if set <> 0 then
                    Posix.free saved
                    Error $"the terminal could not be put in raw mode (errno {Posix.errno ()})"
                else
                    let decoder = Utf8Input()
                    let buffer = Array.zeroCreate<byte> 4096
                    let readable = Posix.alloc Posix.FdSetSize
                    let timeout = Posix.alloc Posix.TimevalSize
                    let restored = ref 0

                    let read () =
                        // Standard input (descriptor 0) in the set; wait at most 100 ms.
                        for i in 0 .. Posix.FdSetSize - 1 do
                            Marshal.WriteByte(readable, i, 0uy)

                        Marshal.WriteInt32(readable, 0, 1)
                        Marshal.WriteInt64(timeout, 0, 0L)
                        Marshal.WriteInt64(timeout, 8, 100_000L)

                        if Posix.select (1, readable, 0n, 0n, timeout) <= 0 then
                            None
                        else
                            let handle = GCHandle.Alloc(buffer, GCHandleType.Pinned)

                            let n =
                                try
                                    Posix.read (0, handle.AddrOfPinnedObject(), unativeint buffer.Length) |> int
                                finally
                                    handle.Free()

                            if n > 0 then
                                Some(decoder.Decode(buffer, n))
                            elif n = 0 then
                                Some ""
                            else
                                let error = Posix.errno ()

                                if error = Posix.EIntr || error = Posix.EAgain then
                                    None
                                else
                                    Some ""

                    Ok
                        {
                            Read = read
                            Write = writeAll
                            Size = size
                            Restore =
                                fun () ->
                                    if Interlocked.Exchange(&restored.contents, 1) = 0 then
                                        Posix.tcsetattr (0, Posix.TcsaNow, saved) |> ignore
                        }

module private Win =
    let private fallback = { Columns = 120; Rows = 30 }

    let private mode (handle: nativeint) : uint32 option =
        let cell = Posix.alloc 4

        try
            if Windows.GetConsoleMode(handle, cell) <> 0 then
                Some(uint32 (Marshal.ReadInt32 cell))
            else
                None
        finally
            Posix.free cell

    let isTerminal () =
        (mode (Windows.GetStdHandle Windows.StdInputHandle)).IsSome
        && (mode (Windows.GetStdHandle Windows.StdOutputHandle)).IsSome

    let openRaw () : Result<Backend, string> =
        let input = Windows.GetStdHandle Windows.StdInputHandle
        let output = Windows.GetStdHandle Windows.StdOutputHandle

        match mode input, mode output with
        | Some inMode, Some outMode ->
            let inCp = Windows.GetConsoleCP()
            let outCp = Windows.GetConsoleOutputCP()

            let rawIn =
                (inMode
                 &&& ~~~(Windows.EnableProcessedInput
                         ||| Windows.EnableLineInput
                         ||| Windows.EnableEchoInput
                         ||| Windows.EnableWindowInput
                         ||| Windows.EnableMouseInput))
                ||| Windows.EnableVirtualTerminalInput

            let rawOut =
                outMode
                ||| Windows.EnableProcessedOutput
                ||| Windows.EnableVirtualTerminalProcessing

            if Windows.SetConsoleMode(output, rawOut) = 0 then
                Error "the console does not take VT sequences (virtual terminal processing)"
            elif Windows.SetConsoleMode(input, rawIn) = 0 then
                Windows.SetConsoleMode(output, outMode) |> ignore
                Error "the console's input could not be put in raw mode"
            else
                Windows.SetConsoleCP Windows.Utf8CodePage |> ignore
                Windows.SetConsoleOutputCP Windows.Utf8CodePage |> ignore
                let records = 128
                let buffer = Posix.alloc (Windows.InputRecordSize * records)
                let cell = Posix.alloc 4
                let restored = ref 0

                let read () =
                    if Windows.WaitForSingleObject(input, 100u) <> Windows.WaitObject0 then
                        None
                    elif Windows.GetNumberOfConsoleInputEvents(input, cell) = 0 then
                        Some ""
                    else
                        let available = Marshal.ReadInt32 cell

                        if available <= 0 then
                            None
                        elif Windows.ReadConsoleInputW(input, buffer, uint32 (min available records), cell) = 0 then
                            Some ""
                        else
                            let count = Marshal.ReadInt32 cell
                            let text = StringBuilder()

                            for i in 0 .. count - 1 do
                                let record = buffer + nativeint (i * Windows.InputRecordSize)

                                if Marshal.ReadInt16(record, 0) = Windows.KeyEvent then
                                    let down = Marshal.ReadInt32(record, Windows.KeyRecord.KeyDown) <> 0
                                    let repeat = max 1 (int (Marshal.ReadInt16(record, Windows.KeyRecord.RepeatCount)))
                                    let c = char (Marshal.ReadInt16(record, Windows.KeyRecord.Char) |> uint16)

                                    if down && c <> '\000' then
                                        for _ in 1..repeat do
                                            text.Append c |> ignore

                            if text.Length = 0 then None else Some(text.ToString())

                let write (text: string) =
                    let chars = text.ToCharArray()
                    let handle = GCHandle.Alloc(chars, GCHandleType.Pinned)
                    let written = Posix.alloc 4

                    try
                        let start = handle.AddrOfPinnedObject()
                        let mutable offset = 0
                        let mutable failed = false

                        while not failed && offset < chars.Length do
                            let length = min 8192 (chars.Length - offset)

                            if
                                Windows.WriteConsoleW(
                                    output,
                                    start + nativeint (offset * 2),
                                    uint32 length,
                                    written,
                                    0n
                                )
                                    =
                                    0
                            then
                                failed <- true
                            else
                                offset <- offset + max 1 (Marshal.ReadInt32 written)
                    finally
                        handle.Free()
                        Posix.free written

                let size () =
                    let info = Posix.alloc Windows.ScreenInfo.Size

                    try
                        if Windows.GetConsoleScreenBufferInfo(output, info) <> 0 then
                            let at (offset: int) = int (Marshal.ReadInt16(info, offset))
                            let columns = at Windows.ScreenInfo.Right - at Windows.ScreenInfo.Left + 1
                            let rows = at Windows.ScreenInfo.Bottom - at Windows.ScreenInfo.Top + 1

                            if columns > 0 && rows > 0 then
                                { Columns = columns; Rows = rows }
                            else
                                fallback
                        else
                            fallback
                    finally
                        Posix.free info

                Ok
                    {
                        Read = read
                        Write = write
                        Size = size
                        Restore =
                            fun () ->
                                if Interlocked.Exchange(&restored.contents, 1) = 0 then
                                    Windows.SetConsoleMode(input, inMode) |> ignore
                                    Windows.SetConsoleMode(output, outMode) |> ignore
                                    Windows.SetConsoleCP inCp |> ignore
                                    Windows.SetConsoleOutputCP outCp |> ignore
                    }
        | _ -> Error "standard input and output are not a console"

/// The controlling terminal, in raw mode with the TUI's screen (spec §19: the TUI runs in the
/// terminal it was started from): termios on Linux and macOS, the console's modes on Windows (virtual
/// terminal input and output, UTF-8 code pages). `Dispose`, the process's exit and an unhandled
/// exception all give the terminal back as it was.
[<Sealed>]
type LocalTty private (backend: Backend, leave: string) =
    let gate = obj ()
    let pieces = Channel.CreateUnbounded<string>()
    let mutable stopped = 0
    let mutable closed = false

    let reader =
        Thread(
            (fun () ->
                while Volatile.Read(&stopped) = 0 && not closed do
                    try
                        match backend.Read() with
                        | Some "" -> closed <- true
                        | Some text -> pieces.Writer.TryWrite text |> ignore
                        | None -> ()
                    with _ ->
                        closed <- true),
            IsBackground = true,
            Name = "tty input"
        )

    let restore () =
        if Interlocked.Exchange(&stopped, 1) = 0 then
            lock gate (fun () ->
                try
                    backend.Write leave
                with _ ->
                    ())

            backend.Restore()

    do
        AppDomain.CurrentDomain.ProcessExit.Add(fun _ -> restore ())
        AppDomain.CurrentDomain.UnhandledException.Add(fun _ -> restore ())
        reader.Start()

    /// Whether standard input and output are a terminal (a console on Windows).
    static member IsTerminal() : bool =
        if OperatingSystem.IsWindows() then
            Win.isTerminal ()
        else
            Unix.isTerminal ()

    /// Puts the terminal in raw mode and writes `enter`; `leave` is written when it is given back.
    static member Open(enter: string, leave: string) : Result<LocalTty, string> =
        let backend =
            if OperatingSystem.IsWindows() then
                Win.openRaw ()
            else
                Unix.openRaw ()

        backend
        |> Result.map (fun backend ->
            backend.Write enter
            new LocalTty(backend, leave))

    member _.Tty: Tty =
        {
            Read = fun cancellation -> pieces.Reader.ReadAsync(cancellation).AsTask()
            Write =
                fun text _ ->
                    if Volatile.Read(&stopped) = 0 then
                        lock gate (fun () -> backend.Write text)

                    Task.FromResult()
            Size = fun _ -> Task.FromResult(backend.Size())
        }

    /// Gives the terminal back: the TUI's screen goes, the modes are as they were.
    member _.Restore() = restore ()

    interface IDisposable with
        member _.Dispose() = restore ()

    override _.ToString() = "LocalTty"
