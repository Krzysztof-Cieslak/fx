// What a terminal looks like on this machine, for docs/spikes/pty.md: raw first output (the VT
// sequences ConPTY adds), terminal queries, latency, throughput, termios defaults and exit codes.
// Prints a Markdown report. Build src/Fx.Pty first:
//
//   dotnet build src/Fx.Pty -c Release && dotnet fsi docs/spikes/pty-probe/probe.fsx
//
// pty-probe.yml runs it on every target. Measurements, not tests: nothing here fails on a value.

#r "../../../artifacts/bin/Fx.Effects/release/Fx.Effects.dll"
#r "../../../artifacts/bin/Fx.Pty/release/Fx.Pty.dll"

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Threading
open Fx.Effects
open Fx.Pty

let windows = OperatingSystem.IsWindows()
let terminal = LocalTerminal.create (LocalTerminal.defaultShell ()) (Path.GetTempPath())
let limit () = new CancellationTokenSource(TimeSpan.FromSeconds 60.0)

let escape (text: string) =
    let sb = StringBuilder()

    for c in text do
        match c with
        | '\u001b' -> sb.Append "\\e" |> ignore
        | '\r' -> sb.Append "\\r" |> ignore
        | '\n' -> sb.Append "\\n" |> ignore
        | '\u0007' -> sb.Append "\\a" |> ignore
        | '|' -> sb.Append "\\|" |> ignore
        | '`' -> sb.Append "'" |> ignore
        | c when c < ' ' -> sb.Append("\\x").Append((int c).ToString("x2")) |> ignore
        | c -> sb.Append c |> ignore

    sb.ToString()

let shorten (n: int) (text: string) =
    if text.Length <= n then text else text.Substring(0, n) + "…"

let spec (program: Program) =
    {
        Program = program
        Cwd = None
        Env = Inherit []
        Size = { Cols = 100; Rows = 30 }
    }

type Run =
    {
        Text: string
        ExitCode: int
        FirstByte: TimeSpan
        Total: TimeSpan
        Bytes: int64
    }

/// Runs a program on a terminal: `interact` drives it once it printed `readyMarker` (if any).
let run (program: Program) (readyMarker: string option) (interact: Pty -> unit) : Run =
    use limit = limit ()
    let clock = Stopwatch.StartNew()

    match (terminal.Spawn (spec program) limit.Token).Result with
    | Error e -> failwith (string e)
    | Ok pty ->
        try
            let text = StringBuilder()
            let mutable first = TimeSpan.Zero
            let mutable bytes = 0L
            let mutable reading = true
            let mutable ready = readyMarker.IsNone

            if ready then
                interact pty

            while reading do
                match pty.Read(limit.Token).Result with
                | Some chunk ->
                    if bytes = 0L then
                        first <- clock.Elapsed

                    bytes <- bytes + int64 chunk.Length

                    if text.Length < 65536 then
                        text.Append(Encoding.UTF8.GetString chunk) |> ignore

                    if not ready && (VtText.plain (text.ToString())).Contains readyMarker.Value then
                        ready <- true
                        interact pty
                | None -> reading <- false

            let code = pty.Wait(limit.Token).Result

            {
                Text = text.ToString()
                ExitCode = code
                FirstByte = first
                Total = clock.Elapsed
                Bytes = bytes
            }
        finally
            pty.Close().Wait()

let ms (t: TimeSpan) = t.TotalMilliseconds.ToString("F1")

let row (cells: string list) =
    printfn "| %s |" (String.concat " | " cells)

let pwsh (script: string) =
    Exec("pwsh", [ "-NoLogo"; "-NoProfile"; "-NonInteractive"; "-Command"; script ])

printfn "%s, %s, %s" RuntimeInformation.OSDescription (string RuntimeInformation.OSArchitecture) RuntimeInformation.RuntimeIdentifier
printfn ""

// 1. Latency and the raw output of a trivial program.
printfn "**Trivial programs** (100x30; time from Spawn to the first byte and to the end of the output, median of 5)"
printfn ""
row [ "Program"; "First byte (ms)"; "End (ms)"; "Exit"; "Raw output (escaped, first 400 chars)" ]
row [ "---"; "---:"; "---:"; "---:"; "---" ]

let trivial =
    if windows then
        [
            "cmd /c echo hi", Exec("cmd", [ "/d"; "/c"; "echo hi" ])
            "pwsh -Command", pwsh "Write-Host hi"
            "powershell -Command", Exec("powershell", [ "-NoLogo"; "-NoProfile"; "-Command"; "Write-Host hi" ])
        ]
    else
        [ "sh -c echo hi", Script "echo hi"; "printf with colour", Exec("printf", [ "\u001b[31mred\u001b[0m\\n" ]) ]

for name, program in trivial do
    let runs = [ for _ in 1..5 -> run program None ignore ]
    let median (f: Run -> TimeSpan) = runs |> List.map f |> List.sort |> List.item 2
    let last = List.last runs
    row [ name; ms (median _.FirstByte); ms (median _.Total); string last.ExitCode; escape (shorten 400 last.Text) ]

printfn ""

// 2. Queries the terminal sends to its client (DSR cursor position, primary device attributes).
let queries (text: string) =
    [
        if text.Contains "\u001b[6n" then "DSR-CPR (ESC[6n)"
        if text.Contains "\u001b[c" || text.Contains "\u001b[0c" then "DA1 (ESC[c)"
        if text.Contains "\u001b[>c" then "DA2 (ESC[>c)"
    ]

let queried =
    trivial
    |> List.collect (fun (_, program) -> queries (run program None ignore).Text)
    |> List.distinct

printfn "**Terminal queries in the output:** %s" (if queried.IsEmpty then "none" else String.concat ", " queried)
printfn ""

// 3. Throughput: about 2 MB of output.
let bulk =
    if windows then
        pwsh "[Console]::Out.Write(('x' * 99 + \"`n\") * 20000)"
    else
        Script "yes \"$(printf '%099d' 0)\" | head -n 20000"

let throughput = run bulk None ignore
let mbs = float throughput.Bytes / 1048576.0 / throughput.Total.TotalSeconds

printfn
    "**Throughput:** %d bytes in %s ms (%s MiB/s, includes the producer)"
    throughput.Bytes
    (ms throughput.Total)
    (mbs.ToString("F1"))

printfn ""

// 4. Exit codes after Ctrl+C and Kill.
let sleeper =
    if windows then
        pwsh "Write-Host '@@ready'; Start-Sleep -Seconds 30"
    else
        Script "echo @@ready; sleep 30"

let interrupted = run sleeper (Some "@@ready") (fun pty -> pty.Write [| 3uy |] CancellationToken.None |> _.Wait())
let killed = run sleeper (Some "@@ready") (fun pty -> pty.Kill().Wait())

printfn
    "**Exit codes:** Ctrl+C %d (after %s ms), Kill %d (after %s ms)"
    interrupted.ExitCode
    (ms interrupted.Total)
    killed.ExitCode
    (ms killed.Total)

printfn ""

// 5. Terminal settings and environment the program sees.
if windows then
    let console =
        run
            (pwsh
                "Write-Host \"TERM=$env:TERM WT_SESSION=$env:WT_SESSION OutputEncoding=$([Console]::OutputEncoding.WebName) VT=$($Host.UI.SupportsVirtualTerminal)\"")
            None
            ignore

    printfn "**Console:** `%s`" (escape ((VtText.plain console.Text).Trim()))
else
    let stty = run (Script "echo \"TERM=${TERM-unset}\"; stty -a") None ignore
    printfn "**Terminal settings** (`stty -a`, TERM inherited from the probe):"
    printfn ""
    printfn "```"
    printfn "%s" ((VtText.plain stty.Text).Trim())
    printfn "```"

printfn ""
