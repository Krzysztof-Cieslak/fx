/// Lists and runs scenarios. Fx.Aot.Diff runs this on the JIT and as a native binary and compares
/// stdout, stderr and the exit code.
module Fx.Aot.Scenarios.Program

let private usage =
    "Usage: Fx.Aot.Scenarios <command>\n\n"
    + "  list               names of the scenarios that run on this platform\n"
    + "  list --canaries    names of the scenarios that must differ (the diff self-test)\n"
    + "  run <name>         run one scenario and print its output\n"
    + "  platform           print the detected platform\n"
    + "  rpc-peer           serve the JSON-RPC test service on stdin and stdout\n"

let private writeLines (lines: string seq) =
    for line in lines do
        stdout.Write(line + "\n")

let private run (scenario: Scenario) : int =
    try
        scenario.Run().GetAwaiter().GetResult() |> writeLines
        0
    with e ->
        // Identical on both sides, but the diff still fails: a scenario must succeed on the JIT.
        writeLines [ $"unhandled {e.GetType().FullName}: {Show.escape e.Message}" ]
        1

[<EntryPoint>]
let main argv =
    let platform = Platform.current ()

    let names (scenarios: Scenario list) =
        scenarios
        |> List.filter (fun s -> s.RunsOn platform)
        |> List.map (fun s -> s.Name)
        |> writeLines

        0

    match List.ofArray argv with
    | [ "list" ] -> names Registry.all
    | [ "list"; "--canaries" ] -> names Registry.canaries
    | [ "run"; name ] ->
        match Registry.tryFind name with
        | None ->
            stderr.Write $"unknown scenario: {name}\n"
            2
        | Some scenario when not (scenario.RunsOn platform) ->
            stderr.Write $"scenario {name} does not run on {platform}\n"
            2
        | Some scenario -> run scenario
    | [ "platform" ] ->
        writeLines [ string platform ]
        0
    | [ "rpc-peer" ] -> Rpc.Peer.serve ()
    | _ ->
        stderr.Write usage
        2
