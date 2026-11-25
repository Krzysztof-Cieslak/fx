namespace Fx.Kernel

open System
open System.Text
open System.Text.Unicode
open Fx.Contracts
open Fx.Rpc

/// A line a kernel wrote to its standard output.
[<RequireQualifiedAccess>]
type KernelLine =
    /// A JSON-RPC message.
    | Frame of string
    /// Bytes that are not part of the protocol: something a cell (or a child process it started)
    /// wrote to file descriptor 1 directly. Invalid UTF-8 becomes U+FFFD.
    | Stray of string

    override this.ToString() =
        match this with
        | Frame text -> $"frame ({text.Length} chars)"
        | Stray text -> $"stray ({text.Length} chars)"

/// The kernel's reading of the stream framing (docs/modules/kernel-protocol.md, "Framing"): kernels
/// speak newline-delimited JSON-RPC and never open binary channels, but code running in a kernel can
/// write to its standard output past the kernel. So every message starts with the literal
/// `{"jsonrpc":"2.0",` (our writer puts `jsonrpc` first) on a line of its own, and the reader takes
/// a message from where that prefix starts, reports everything else as stray output, and goes on.
/// A zero byte is plain text here (no channels), and a long line without the prefix is reported in
/// pieces instead of failing. Only a message larger than the limit ends the connection. Not
/// thread-safe: one reader.
type KernelLineReader(maxText: int, maxStray: int) =
    static let prefix = Encoding.UTF8.GetBytes "{\"jsonrpc\":\"2.0\","

    let mutable buffer = Array.zeroCreate<byte> 4096
    let mutable count = 0
    /// Bytes of the current line already searched for a newline.
    let mutable scanned = 0
    /// Bytes of the current line already searched for the prefix.
    let mutable prefixScanned = 0
    /// The current line starts with the prefix: a message is arriving.
    let mutable inFrame = false

    let lossy (bytes: ReadOnlySpan<byte>) = Encoding.UTF8.GetString bytes

    let indexOfPrefix (span: ReadOnlySpan<byte>) = span.IndexOf(ReadOnlySpan prefix)

    let consume (n: int) =
        Buffer.BlockCopy(buffer, n, buffer, 0, count - n)
        count <- count - n
        scanned <- 0
        prefixScanned <- 0
        inFrame <- false

    let ensure (extra: int) =
        if count + extra > buffer.Length then
            let larger = Array.zeroCreate<byte>(max (count + extra) (buffer.Length * 2))
            Buffer.BlockCopy(buffer, 0, larger, 0, count)
            buffer <- larger

    /// One complete line, without its `\n`.
    let classify (line: ReadOnlySpan<byte>) (output: ResizeArray<KernelLine>) : TransportError option =
        let line =
            if line.Length > 0 && line[line.Length - 1] = 13uy then
                line.Slice(0, line.Length - 1)
            else
                line

        if line.Length = 0 then
            None
        else
            match indexOfPrefix line with
            | -1 ->
                output.Add(KernelLine.Stray(lossy line))
                None
            | at ->
                if at > 0 then
                    output.Add(KernelLine.Stray(lossy (line.Slice(0, at))))

                let frame = line.Slice at

                if frame.Length > maxText then
                    Some(TransportError.TooLarge maxText)
                elif not (Utf8.IsValid frame) then
                    output.Add(KernelLine.Stray(lossy frame))
                    None
                else
                    let text = Encoding.UTF8.GetString frame

                    match Json.parse text with
                    | Ok _ -> output.Add(KernelLine.Frame text)
                    | Error _ -> output.Add(KernelLine.Stray text)

                    None

    /// Adds bytes and returns the lines they complete, or why the stream is broken.
    member this.Feed(chunk: ReadOnlySpan<byte>) : Result<KernelLine list, TransportError> =
        ensure chunk.Length
        chunk.CopyTo(Span(buffer, count, chunk.Length))
        count <- count + chunk.Length
        let output = ResizeArray()
        let mutable error = None
        let mutable searching = true

        while searching && error.IsNone do
            let pending = ReadOnlySpan(buffer, scanned, count - scanned)

            match pending.IndexOf 10uy with
            | -1 ->
                scanned <- count
                searching <- false
            | offset ->
                let length = scanned + offset
                error <- classify (ReadOnlySpan(buffer, 0, length)) output
                consume (length + 1)

        // A partial line: report stray text before a message, or the head of a long stray line
        // (keeping enough to find a prefix that the next bytes complete). Each byte is searched once.
        if error.IsNone && count > 0 && not inFrame then
            let from = max 0 (prefixScanned - (prefix.Length - 1))

            match indexOfPrefix (ReadOnlySpan(buffer, from, count - from)) with
            | -1 ->
                prefixScanned <- count

                if count > maxStray then
                    let keep = prefix.Length - 1
                    output.Add(KernelLine.Stray(lossy (ReadOnlySpan(buffer, 0, count - keep))))
                    consume (count - keep)
                    scanned <- count
                    prefixScanned <- count
            | found ->
                let at = from + found

                if at > 0 then
                    output.Add(KernelLine.Stray(lossy (ReadOnlySpan(buffer, 0, at))))
                    consume at
                    scanned <- count

                inFrame <- true

        // `\r` may follow the largest message.
        if error.IsNone && inFrame && count > maxText + 1 then
            error <- Some(TransportError.TooLarge maxText)

        match error with
        | Some e -> Error e
        | None -> Ok(List.ofSeq output)

    /// The end of the stream: a last line without `\n` still counts.
    member _.End() : Result<KernelLine list, TransportError> =
        let output = ResizeArray()
        let error = classify (ReadOnlySpan(buffer, 0, count)) output
        consume count

        match error with
        | Some e -> Error e
        | None -> Ok(List.ofSeq output)

    override _.ToString() =
        $"KernelLineReader({count} bytes pending)"

module KernelFraming =
    /// What every message from a kernel starts with.
    [<Literal>]
    let Prefix = "{\"jsonrpc\":\"2.0\","

    /// Splits a whole output (e.g. a recording) into lines, as the transport reads them.
    let split (maxText: int) (bytes: byte array) : Result<KernelLine list, TransportError> =
        let reader = KernelLineReader(maxText, 64 * 1024)

        match reader.Feed(ReadOnlySpan bytes) with
        | Error e -> Error e
        | Ok lines ->
            match reader.End() with
            | Error e -> Error e
            | Ok rest -> Ok(lines @ rest)
