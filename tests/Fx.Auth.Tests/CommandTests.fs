/// `auth` on the command line: parsing, and what each command prints (never a secret).
module Fx.Auth.Tests.CommandTests

open System.Threading.Tasks
open Expecto
open Fx
open Fx.Effects
open Fx.Sdk
open Fx.Auth
open Fx.Auth.Tests.Support

let private providers =
    [ Fx.Ext.ModelAnthropic.Provider.def; Fx.Ext.ModelOpenAI.Provider.def ]

/// A terminal where the person pastes `text`, and the flows it was asked to run.
let private pasting (text: string) =
    let shown = ResizeArray<AuthFlow>()

    let runner: FlowRunner =
        fun flow _ ->
            shown.Add flow
            Task.FromResult(Ok(FlowResult.Pasted(SecretText text)))

    runner, shown

let private refusing: FlowRunner =
    fun _ _ -> Task.FromResult(Error "the person pressed Ctrl+C")

let private runCommand (service: AuthService) (runner: FlowRunner) (args: string list) =
    match AuthCommand.parse args with
    | Ok command -> run (AuthCli.run service providers runner command)
    | Error e -> failtest e

[<Tests>]
let tests =
    let bin = Product.binaryName

    testList
        "auth commands"
        [
            test "arguments parse into commands, and mistakes say what is expected" {
                Expect.equal
                    (AuthCommand.parse [ "login"; "anthropic" ])
                    (Ok(AuthCommand.Login("anthropic", None)))
                    "login"

                Expect.equal
                    (AuthCommand.parse [ "login"; "openai"; "--method"; "api_key" ])
                    (Ok(AuthCommand.Login("openai", Some "api_key")))
                    "login with a method"

                Expect.equal
                    (AuthCommand.parse [ "login"; "--method"; "api_key"; "openai" ])
                    (Ok(AuthCommand.Login("openai", Some "api_key")))
                    "the method first"

                Expect.equal (AuthCommand.parse [ "logout"; "openai" ]) (Ok(AuthCommand.Logout "openai")) "logout"
                Expect.equal (AuthCommand.parse [ "list" ]) (Ok AuthCommand.List) "list"
                Expect.equal (AuthCommand.parse [ "status" ]) (Ok AuthCommand.Status) "status"

                for args, problem in
                    [
                        [], "needs a command"
                        [ "login" ], "usage: auth login"
                        [ "login"; "--method"; "x" ], "usage: auth login"
                        [ "logout" ], "usage: auth logout"
                        [ "logout"; "a"; "b" ], "usage: auth logout"
                        [ "whoami" ], "unknown auth command whoami"
                    ] do
                    match AuthCommand.parse args with
                    | Error e -> Expect.stringContains e problem (String.concat " " args)
                    | Ok command -> failtest $"{args}: expected an error, got {command}"

                Expect.equal AuthCommand.usage.Length 4 "usage"
                Expect.stringStarts AuthCommand.usage[0] (bin + " auth login") "usage names the binary"
            }

            test "login pastes the key into the store, and says where it went" {
                let service = start (fakes ()) []
                let runner, shown = pasting "sk-ant-cli-secret\n"
                let code, lines = runCommand service runner [ "login"; "anthropic" ]
                Expect.equal code 0 "exit code"
                Expect.equal (List.ofSeq shown) [ AuthFlow.Paste "Paste the API key for anthropic." ] "the flow"

                Expect.equal
                    lines
                    [
                        "Stored the api_key credential for anthropic in the keyring store (memory) as anthropic."
                    ]
                    "output"

                let code, lines = runCommand service refusing [ "login"; "openai" ]
                Expect.equal (code, lines) (1, [ "Login cancelled: the person pressed Ctrl+C." ]) "cancelled"

                let code, lines = runCommand service refusing [ "login"; "mistral" ]
                Expect.equal code 1 "unknown provider"
                Expect.equal lines [ "No provider is named mistral (providers: anthropic, openai)." ] "lists them"
            }

            test "a file store's notice comes with the first login" {
                let headless = fakes ()
                headless.Keyring.Status <- KeyringStatus.Unavailable KeyringUnavailable.NoSessionBus
                let service = start headless []
                let runner, _ = pasting "sk-x"
                let code, lines = runCommand service runner [ "login"; "openai" ]
                Expect.equal code 0 "exit code"

                match lines with
                | [ stored; note ] ->
                    Expect.equal
                        stored
                        "Stored the api_key credential for openai in the file store (memory) as openai."
                        "stored"

                    Expect.stringStarts note "Note: credentials are kept in a file only you can read" "notice"
                | other -> failtest $"expected two lines, got {other}"
            }

            test "list, status and logout print no secret" {
                let host =
                    { fakes () with
                        Variables = [ "OPENAI_API_KEY", "sk-oai-env-secret" ]
                    }

                let service = start host []
                let runner, _ = pasting "sk-ant-cli-secret"
                runCommand service runner [ "login"; "anthropic" ] |> ignore

                let _, list = runCommand service refusing [ "list" ]
                Expect.equal list [ "Credentials in the keyring store (memory):"; "  anthropic  api_key" ] "list"

                let _, status = runCommand service refusing [ "status" ]

                Expect.equal
                    status
                    [
                        "Credential store: keyring (memory), auto: the OS keyring is ready"
                        "Store status: ready"
                        "  anthropic  api_key"
                        "  openai     env"
                    ]
                    "status"

                let _, logout = runCommand service refusing [ "logout"; "anthropic" ]
                Expect.equal logout [ "Removed anthropic from the keyring store (memory)." ] "logout"
                let _, again = runCommand service refusing [ "logout"; "anthropic" ]
                Expect.equal again [ "No stored credentials for anthropic in the keyring store (memory)." ] "again"
                let _, empty = runCommand service refusing [ "list" ]
                Expect.equal empty [ "No credentials in the keyring store (memory)." ] "empty"

                for line in list @ status @ logout do
                    Expect.isFalse (line.Contains "secret") line
            }

            test "status says what is missing" {
                let service = start (fakes ()) []
                let _, status = runCommand service refusing [ "status" ]

                Expect.stringContains
                    status[2]
                    ("no credentials: no API key for anthropic: run `"
                     + bin
                     + " auth login anthropic`; or ANTHROPIC_API_KEY is not set")
                    "anthropic"
            }
        ]
