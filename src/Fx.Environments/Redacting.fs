namespace Fx.Environments

open System.Collections.Generic
open Fx.Effects
open Fx.Envd
open Fx.Pty

/// An environment's capability records with the credentials its commands got redacted (T138, spec
/// §18): whatever a turn reads from the environment (process and terminal output, kernels' output,
/// files, search results) never holds one of the values. Writes and process input pass unchanged.
[<RequireQualifiedAccess>]
module Redacting =
    /// A process whose output is redacted: each stream on its own, the end of a piece that could begin
    /// a value held back until more output or the end.
    let proc (redaction: Redaction) (inner: Proc) : Proc =
        let mutable stdout: byte array = [||]
        let mutable stderr: byte array = [||]
        let queued = Queue<OutputChunk>()
        let mutable ended = false

        { inner with
            Read =
                fun cancellation ->
                    task {
                        let mutable result = None
                        let mutable finished = false

                        while result.IsNone && not finished do
                            if queued.Count > 0 then
                                result <- Some(queued.Dequeue())
                            elif ended then
                                finished <- true
                            else
                                match! inner.Read cancellation with
                                | Some(Stdout bytes) ->
                                    let ready, held = Redaction.step redaction stdout bytes
                                    stdout <- held

                                    if ready.Length > 0 then
                                        result <- Some(Stdout ready)
                                | Some(Stderr bytes) ->
                                    let ready, held = Redaction.step redaction stderr bytes
                                    stderr <- held

                                    if ready.Length > 0 then
                                        result <- Some(Stderr ready)
                                | None ->
                                    ended <- true

                                    if stdout.Length > 0 then
                                        queued.Enqueue(Stdout(Redaction.flush redaction stdout))

                                    if stderr.Length > 0 then
                                        queued.Enqueue(Stderr(Redaction.flush redaction stderr))

                                    stdout <- [||]
                                    stderr <- [||]

                        return result
                    }
        }

    /// A shell whose commands' and processes' output is redacted.
    let shell (redaction: Redaction) (inner: Shell) : Shell =
        if redaction.IsEmpty then
            inner
        else
            {
                Run =
                    fun command cancellation ->
                        task {
                            match! inner.Run command cancellation with
                            | Ok output ->
                                return
                                    Ok
                                        { output with
                                            Stdout = Redaction.bytes redaction output.Stdout
                                            Stderr = Redaction.bytes redaction output.Stderr
                                        }
                            | Error e -> return Error e
                        }
                Spawn =
                    fun spec cancellation ->
                        task {
                            match! inner.Spawn spec cancellation with
                            | Ok started -> return Ok(proc redaction started)
                            | Error e -> return Error e
                        }
            }

    /// A terminal session whose output is redacted.
    let pty (redaction: Redaction) (inner: Pty) : Pty =
        let mutable pending: byte array = [||]
        let mutable ended = false

        { inner with
            Read =
                fun cancellation ->
                    task {
                        let mutable result = None

                        while result.IsNone && not ended do
                            match! inner.Read cancellation with
                            | Some bytes ->
                                let ready, held = Redaction.step redaction pending bytes
                                pending <- held

                                if ready.Length > 0 then
                                    result <- Some ready
                            | None ->
                                ended <- true

                                if pending.Length > 0 then
                                    result <- Some(Redaction.flush redaction pending)
                                    pending <- [||]

                        return result
                    }
        }

    let terminal (redaction: Redaction) (inner: Terminal) : Terminal =
        if redaction.IsEmpty then
            inner
        else
            {
                Spawn =
                    fun spec cancellation ->
                        task {
                            match! inner.Spawn spec cancellation with
                            | Ok started -> return Ok(pty redaction started)
                            | Error e -> return Error e
                        }
            }

    let fsRead (redaction: Redaction) (inner: FsRead) : FsRead =
        if redaction.IsEmpty then
            inner
        else
            { inner with
                ReadFile =
                    fun path cancellation ->
                        task {
                            match! inner.ReadFile path cancellation with
                            | Ok bytes -> return Ok(Redaction.bytes redaction bytes)
                            | Error e -> return Error e
                        }
            }

    /// File ranges keep their length (readers count on it: values are masked with something as long),
    /// and are read with enough around them to find a value that crosses their ends.
    let fsOps (redaction: Redaction) (inner: FsOps) : FsOps =
        if redaction.IsEmpty then
            inner
        else
            let reach = Redaction.longest redaction - 1

            { inner with
                ReadRange =
                    fun path offset count cancellation ->
                        task {
                            let back = min offset (int64 reach)

                            match! inner.ReadRange path (offset - back) (count + int back + reach) cancellation with
                            | Ok chunk ->
                                let masked = Redaction.sameLength redaction chunk.Content
                                let start = min (int back) masked.Length
                                let length = max 0 (min count (masked.Length - start))

                                return
                                    Ok
                                        { chunk with
                                            Offset = offset
                                            Content = Array.sub masked start length
                                        }
                            | Error e -> return Error e
                        }
                Search =
                    fun query cancellation ->
                        task {
                            match! inner.Search query cancellation with
                            | Ok result ->
                                return
                                    Ok
                                        { result with
                                            Matches =
                                                result.Matches
                                                |> List.map (fun m ->
                                                    { m with
                                                        Text = Redaction.text redaction m.Text
                                                    })
                                        }
                            | Error e -> return Error e
                        }
            }

    /// An environment's client with every way out redacted.
    let client (redaction: Redaction) (inner: EnvClient) : EnvClient =
        if redaction.IsEmpty then
            inner
        else
            { inner with
                FsRead = fsRead redaction inner.FsRead
                FsOps = fsOps redaction inner.FsOps
                Shell = shell redaction inner.Shell
                Terminal = terminal redaction inner.Terminal
            }
