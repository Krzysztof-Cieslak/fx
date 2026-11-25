namespace Fx.Envd.Live

open System.IO
open System.Threading
open System.Threading.Tasks
open Fx.Envd

/// Spill files of process output in a directory of envd's state (one file per stream, named by
/// envd). The only file that writes them; envd empties the directory when it starts.
module LocalSpill =
    let private readUpTo (stream: Stream) (offset: int64) (count: int) (cancellation: CancellationToken) =
        task {
            let size = stream.Length
            let start = min offset size
            let buffer = Array.zeroCreate<byte>(int (min (int64 count) (size - start)))
            stream.Seek(start, SeekOrigin.Begin) |> ignore
            let mutable filled = 0
            let mutable reading = buffer.Length > 0

            while reading do
                let! read = stream.ReadAsync(buffer, filled, buffer.Length - filled, cancellation)
                filled <- filled + read
                reading <- read > 0 && filled < buffer.Length

            return
                if filled = buffer.Length then
                    buffer
                else
                    Array.sub buffer 0 filled
        }

    /// Spill files in `directory` (absolute), which must exist.
    let create (directory: string) : Spill =
        {
            Create =
                fun name _ ->
                    let path = Path.Combine(directory, name)

                    let stream =
                        new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read ||| FileShare.Delete)

                    let gate = new SemaphoreSlim(1, 1)
                    let deleted = ref false

                    let exclusive (work: unit -> Task<'T>) (cancellation: CancellationToken) : Task<'T> =
                        task {
                            do! gate.WaitAsync cancellation

                            try
                                return! work ()
                            finally
                                gate.Release() |> ignore
                        }

                    Task.FromResult
                        {
                            Path = path
                            Append =
                                fun bytes cancellation ->
                                    exclusive
                                        (fun () ->
                                            task {
                                                if not deleted.Value then
                                                    stream.Seek(0L, SeekOrigin.End) |> ignore
                                                    do! stream.WriteAsync(bytes, 0, bytes.Length, cancellation)
                                                    // Flushed to the OS, so the file can be grepped meanwhile.
                                                    do! stream.FlushAsync cancellation
                                            })
                                        cancellation
                            ReadAt =
                                fun offset count cancellation ->
                                    exclusive
                                        (fun () ->
                                            if deleted.Value then
                                                Task.FromResult [||]
                                            else
                                                readUpTo stream offset count cancellation)
                                        cancellation
                            Delete =
                                fun () ->
                                    exclusive
                                        (fun () ->
                                            if not deleted.Value then
                                                deleted.Value <- true
                                                stream.Dispose()

                                                try
                                                    File.Delete path
                                                with
                                                | :? IOException
                                                | :? System.UnauthorizedAccessException -> ()

                                            Task.FromResult())
                                        CancellationToken.None
                        }
        }
