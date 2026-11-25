module Fx.Models.Tests.FitReplayTests

open Expecto
open Fx.Models
open Fx.Models.Tests.Support
open Fx.Models.Tests.Samples

let private fit (info: ModelInfo) (request: ChatRequest) =
    match Fit.request info request with
    | Ok fitted -> fitted
    | Error e -> failtest (string e)

let private textOnly: ModelInfo =
    ModelInfo.create (ModelRef.create "local" "tiny") { Context = 8192; Output = 1024 }

[<Tests>]
let tests =
    testList
        "Fitting and replay"
        [
            test "a request the model can take is unchanged" {
                let plain =
                    { Support.request [ Message.userText "hi" ] with
                        Model = info.Ref
                        MaxOutputTokens = Some 1000
                    }

                Expect.equal (fit info plain) (plain, []) "unchanged"
            }

            test "what the model does not take is dropped, clamped or replaced, and reported" {
                let tooling =
                    { info with
                        Caps =
                            { info.Caps with
                                Tools = { info.Caps.Tools with Forced = false }
                                Media = [ MediaKind.Pdf ]
                                Caching =
                                    { info.Caps.Caching with
                                        MaxBreakpoints = 2
                                    }
                            }
                    }

                let fitted, changes =
                    fit
                        tooling
                        { request with
                            MaxOutputTokens = Some 100_000
                        }

                Expect.equal
                    (changes |> List.map string)
                    [
                        "dropped seed"
                        "max output 100000 clamped to 64000"
                        "omitted image input"
                        "omitted image input"
                        "dropped 1 cache breakpoints"
                        "dropped the cache key"
                        "tools are not strict"
                        "tool choice tool get_weather became auto"
                    ]
                    "changes"

                Expect.equal fitted.Sampling.Seed None "seed"
                Expect.equal fitted.MaxOutputTokens (Some 64_000) "max output"
                Expect.equal fitted.Cache.Tools None "the earliest breakpoint went"
                Expect.equal fitted.ToolChoice ToolChoice.Auto "choice"

                let firstUser = fitted.Messages.Head.Parts
                Expect.equal firstUser[1] (Part.text (Fit.omitted MediaKind.Image)) "image replaced"

                match firstUser[2] with
                | Part.Media m -> Expect.equal m.Kind MediaKind.Pdf "PDFs stay"
                | other -> failtest $"got {other}"
            }

            test "efforts map to the nearest the model has; budgets clamp; reasoning goes for models without it" {
                let ask effort budget =
                    { Support.request [] with
                        Model = info.Ref
                        Reasoning =
                            Some
                                {
                                    Effort = Some effort
                                    BudgetTokens = budget
                                    Visible = false
                                }
                    }

                let _, max = fit info (ask Effort.Max (Some 100_000))

                Expect.equal
                    (max |> List.map string)
                    [ "effort max became high"; "thinking budget 100000 clamped to 32000" ]
                    "max"

                let _, minimal = fit info (ask Effort.Minimal None)
                Expect.equal (minimal |> List.map string) [ "effort minimal became low" ] "minimal"

                let fitted, changes = fit textOnly (ask Effort.High None)
                Expect.equal fitted.Reasoning None "dropped"
                Expect.equal changes [ Adjustment.ReasoningDropped ] "reported"
            }

            test "requests a model cannot serve at all are errors" {
                match Fit.request textOnly request with
                | Error(Unavailable message) -> Expect.stringContains message "does not call tools" "tools"
                | other -> failtest $"got {other}"

                let structured =
                    { Support.request [] with
                        Output = OutputFormat.Json("x", Fx.Contracts.Json.Object [], false)
                    }

                match Fit.request textOnly structured with
                | Error(Unavailable message) -> Expect.stringContains message "structured output" "structured"
                | other -> failtest $"got {other}"
            }

            test "state replays only where the rules allow" {
                let mine = state "sig"
                let target = mine.Model
                let family = mine.Family
                Expect.isTrue (Replay.allows Replay.defaults target family mine) "same model"

                Expect.isFalse
                    (Replay.allows Replay.defaults (ModelRef.create "anthropic" "claude-opus-4-5") family mine)
                    "another model"

                let wide =
                    { Replay.defaults with
                        Families = [ family, ReplayScope.SameFamily ]
                    }

                Expect.isTrue (Replay.allows wide (ModelRef.create "bedrock" "claude") family mine) "same family"
                Expect.isFalse (Replay.allows wide target "openai.responses" mine) "never another family"
            }

            test "moving a conversation to another model drops what it cannot replay" {
                let other = ModelRef.create "openai" "gpt-5.1"

                let messages, changes =
                    Replay.prepare Replay.defaults other "openai.responses" request.Messages

                Expect.equal (changes |> List.map string) [ "message 1 part 0: reasoning dropped" ] "changes"
                Expect.equal (messages[1].Parts |> List.length) 2 "reasoning gone"

                let asText =
                    { Replay.defaults with
                        Reasoning = UnreplayableReasoning.AsText
                    }

                let messages, _ = Replay.prepare asText other "openai.responses" request.Messages
                Expect.equal messages[1].Parts.Head (Part.text "A cat; then the tool.") "kept as text"

                let same, none =
                    Replay.prepare Replay.defaults request.Model "anthropic.messages" request.Messages

                Expect.equal (same, none) (request.Messages, []) "the same model keeps everything"
            }
        ]
