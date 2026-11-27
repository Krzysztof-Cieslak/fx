/// Chosen variables (T138): sources, checks, precedence, consent, the record, and redaction.
module Fx.Environments.Tests.VariablesTests

open System.Collections.Generic
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Envd
open Fx.Environments
open Fx.Sdk

let private utf8 = UTF8Encoding(false)
let private token = "ghp_0123456789abcdef"
let private redaction = Redaction.create [ "GH_TOKEN", token ]

/// Redacts pieces as a reader gets them, then the end.
let private streamed (redaction: Redaction) (pieces: string list) : string =
    let mutable pending = [||]
    let out = ResizeArray<byte>()

    for piece in pieces do
        let ready, held = Redaction.step redaction pending (utf8.GetBytes piece)
        out.AddRange ready
        pending <- held

    out.AddRange(Redaction.flush redaction pending)
    utf8.GetString(out.ToArray())

/// A process that writes these pieces, then ends.
let private fakeProc (chunks: OutputChunk list) : Proc =
    let queue = Queue<OutputChunk>(chunks)

    {
        Pid = 1
        Write = fun _ _ -> Task.FromResult()
        CloseStdin = fun () -> Task.FromResult()
        Read = fun _ -> Task.FromResult(if queue.Count > 0 then Some(queue.Dequeue()) else None)
        Wait = fun _ -> Task.FromResult 0
        Kill = fun () -> Task.FromResult()
    }

let private readAll (proc: Proc) : string * string =
    let out = StringBuilder()
    let err = StringBuilder()
    let mutable go = true

    while go do
        match (proc.Read CancellationToken.None).Result with
        | Some(Stdout bytes) -> out.Append(utf8.GetString bytes) |> ignore
        | Some(Stderr bytes) -> err.Append(utf8.GetString bytes) |> ignore
        | None -> go <- false

    string out, string err

[<Tests>]
let tests =
    testList
        "variables"
        [
            test "host: and secret: are references, without case; anything else is a literal" {
                Expect.equal (EnvVariables.parse "host:GH_TOKEN") (VariableSource.Host "GH_TOKEN") "host"
                Expect.equal (EnvVariables.parse "HOST:GH_TOKEN") (VariableSource.Host "GH_TOKEN") "case"
                Expect.equal (EnvVariables.parse "Secret:npm") (VariableSource.Secret "npm") "secret"
                Expect.equal (EnvVariables.parse "debug") (VariableSource.Literal "debug") "literal"

                Expect.equal
                    (VariableSource.Literal "value").Reference
                    "literal"
                    "a literal's value is never its reference"
            }

            test "a credential written as a literal, an injection vector and bad names are refused" {
                let issues =
                    EnvVariables.check (
                        Map.ofList
                            [
                                "GH_TOKEN", "ghp_literal_value"
                                "NODE_OPTIONS", "--require x"
                                "1BAD", "x"
                                "NPM_TOKEN", "secret:"
                                "OTHER", "secret:-bad"
                                "RUST_LOG", "debug"
                                "GITHUB_TOKEN", "host:GH_TOKEN"
                            ]
                    )
                    |> List.map (fun i -> JsonPath.render i.Path)

                Expect.equal
                    (List.sort issues)
                    (List.sort [ "$.GH_TOKEN"; "$.NODE_OPTIONS"; "$[\"1BAD\"]"; "$.NPM_TOKEN"; "$.OTHER" ])
                    "issues"
            }

            test "a spec's variables are checked as config is read, with paths into the file" {
                match
                    EnvSpecs.parse
                        """{"environments": {"dev": {"provider": "local", "variables": {"GH_TOKEN": "ghp_x", "BUN_OPTIONS": "host:X"}}}}"""
                with
                | Ok specs -> failtest $"expected issues, got {specs}"
                | Error issues ->
                    let paths = issues |> List.map (fun i -> JsonPath.render i.Path) |> List.sort

                    Expect.equal
                        paths
                        [
                            "$.environments.dev.variables.BUN_OPTIONS"
                            "$.environments.dev.variables.GH_TOKEN"
                        ]
                        "paths"
            }

            test "the person's variables win over the spec's; env.deny removes from both" {
                let kept, denied =
                    EnvVariables.requests
                        (Map.ofList [ "GH_TOKEN", "host:MY_GH"; "AWS_KEY_ID", "host:AWS_KEY_ID" ])
                        (Map.ofList [ "gh_token", "host:OTHER"; "RUST_LOG", "debug"; "AWS_REGION", "eu" ])
                        VariableOrigin.ProjectSpec
                        [ "AWS_*" ]

                Expect.equal
                    (kept |> List.map (fun r -> r.Name, r.Source.Reference, r.Origin))
                    [
                        "GH_TOKEN", "host:MY_GH", VariableOrigin.Person
                        "RUST_LOG", "literal", VariableOrigin.ProjectSpec
                    ]
                    "kept"

                Expect.equal (denied |> List.map _.Name) [ "AWS_KEY_ID"; "AWS_REGION" ] "denied"
            }

            test "only a project's credentials need consent" {
                let needs origin =
                    EnvVariables.requests
                        Map.empty
                        (Map.ofList [ "GH_TOKEN", "host:GH_TOKEN"; "NPM", "secret:npm"; "RUST_LOG", "debug" ])
                        origin
                        []
                    |> fst
                    |> List.map (fun r -> r.Name, r.NeedsConsent, r.Credential)

                Expect.equal
                    (needs VariableOrigin.ProjectSpec)
                    [ "GH_TOKEN", true, true; "NPM", true, true; "RUST_LOG", false, false ]
                    "project"

                Expect.equal
                    (needs VariableOrigin.HostSpec)
                    [ "GH_TOKEN", false, true; "NPM", false, true; "RUST_LOG", false, false ]
                    "host config"
            }

            test "a credential shorter than 8 characters is not passed, and the record says why" {
                let request =
                    EnvVariables.requests
                        (Map.ofList [ "GH_TOKEN", "host:GH_TOKEN" ])
                        Map.empty
                        VariableOrigin.HostSpec
                        []
                    |> fst
                    |> List.head

                Expect.isError (EnvVariables.accept request "short") "short"
                Expect.isOk (EnvVariables.accept request token) "long enough"
            }

            test "a variable's record never holds its value" {
                let variable: EnvVariable =
                    {
                        Name = "GH_TOKEN"
                        Source = "host:GH_TOKEN"
                        Origin = VariableOrigin.ProjectSpec
                        Credential = true
                        Outcome = VariableOutcome.Granted "g1"
                    }

                let json = Json.serializeCanonical (EnvVariables.codec.Encode variable)

                Expect.equal
                    json
                    """{"credential":true,"name":"GH_TOKEN","origin":"project","outcome":{"grant":"g1","kind":"granted"},"source":"host:GH_TOKEN"}"""
                    "json"

                Expect.equal
                    (Codec.decode EnvVariables.codec (EnvVariables.codec.Encode variable))
                    (Ok variable)
                    "round trip"
            }

            test "values are redacted, also split across pieces" {
                Expect.equal (Redaction.text redaction $"token={token}!") "token=[redacted GH_TOKEN]!" "whole"

                Expect.equal
                    (streamed redaction [ "a ghp_0123"; "456789ab"; $"cdef b" ])
                    "a [redacted GH_TOKEN] b"
                    "split"

                Expect.equal
                    (streamed redaction [ "line\n"; "ghp_01" ])
                    "line\nghp_01"
                    "a partial value at the end is passed on at the end"
            }

            test "only the end that could begin a value is held back" {
                let ready, held = Redaction.step redaction [||] (utf8.GetBytes "{\"ok\":true}\n")
                Expect.equal (utf8.GetString ready) "{\"ok\":true}\n" "a line passes at once"
                Expect.isEmpty held "nothing held"

                let ready, held = Redaction.step redaction [||] (utf8.GetBytes "out ghp_")
                Expect.equal (utf8.GetString ready) "out " "before"
                Expect.equal (utf8.GetString held) "ghp_" "held"
            }

            test "several values, one inside another, are one region" {
                let both =
                    Redaction.create [ "LONG_TOKEN", "abcdefgh12345678"; "SHORT_TOKEN", "12345678" ]

                Expect.equal
                    (Redaction.text both "x abcdefgh12345678 y 12345678")
                    "x [redacted LONG_TOKEN] y [redacted SHORT_TOKEN]"
                    "regions"

                Expect.equal (streamed both [ "x abcdefgh1234"; "5678 y" ]) "x [redacted LONG_TOKEN] y" "streamed"
            }

            test "values that overlap are one region, named by both" {
                let overlapping =
                    Redaction.create [ "A_TOKEN", "12345678"; "B_TOKEN", "5678ABCDEFGH" ]

                Expect.equal
                    (Redaction.text overlapping "-12345678ABCDEFGH-")
                    "-[redacted A_TOKEN,B_TOKEN]-"
                    "one region"
            }

            test "a value inside a kernel's JSON keeps the JSON valid" {
                let line =
                    Json.serializeCanonical (Json.Object [ "stdout", Json.String $"echo {token}\n" ])

                let redacted = Redaction.text redaction line

                match Json.parse redacted with
                | Ok(Json.Object [ "stdout", Json.String text ]) ->
                    Expect.equal text "echo [redacted GH_TOKEN]\n" "text"
                | other -> failtest $"not the JSON: {other}"
            }

            test "pieces of a file keep their length" {
                let input = utf8.GetBytes $"a={token};"
                let masked = Redaction.sameLength redaction input
                Expect.equal masked.Length input.Length "length"
                Expect.isFalse ((utf8.GetString masked).Contains token) "masked"
            }

            test "a process's output is redacted per stream, also split" {
                let proc =
                    Redacting.proc
                        redaction
                        (fakeProc
                            [
                                Stdout(utf8.GetBytes "x ghp_01234")
                                Stderr(utf8.GetBytes "err ")
                                Stdout(utf8.GetBytes "56789abcdef y")
                            ])

                Expect.equal (readAll proc) ("x [redacted GH_TOKEN] y", "err ") "output"
            }

            test "files read through the client are redacted, ranges by length" {
                let content = utf8.GetBytes $"head {token} tail"

                let fsRead: FsRead =
                    {
                        ReadFile = fun _ _ -> Task.FromResult(Ok content)
                        Stat = fun _ _ -> failwith "unused"
                        List = fun _ _ -> failwith "unused"
                    }

                match
                    (Redacting.fsRead redaction fsRead).ReadFile "f" CancellationToken.None
                    |> _.Result
                with
                | Ok bytes -> Expect.equal (utf8.GetString bytes) "head [redacted GH_TOKEN] tail" "file"
                | Error e -> failtest (string e)
            }

            test "nothing to redact leaves the capabilities as they are" {
                let shell: Shell =
                    {
                        Run = fun _ _ -> failwith "unused"
                        Spawn = fun _ _ -> failwith "unused"
                    }

                Expect.isTrue (obj.ReferenceEquals(Redacting.shell Redaction.none shell, shell)) "same shell"
            }
        ]
