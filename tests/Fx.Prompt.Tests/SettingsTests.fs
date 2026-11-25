/// The render's variant slots and settings.
module Fx.Prompt.Tests.SettingsTests

open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Models
open Fx.Variants
open Fx.Variants.Fakes
open Fx.Prompt

type private Env =
    {
        Variants: Variants
        Scope: Scope
    }

    interface IVariants with
        member env.Variants = env.Variants

    interface IScopeWith<Env> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override _.ToString() = "Env"

let private read (selections: (string * Json) list) : PromptSettings =
    let env =
        {
            Variants = FakeVariants.create (Slots.all @ PromptSlots.all) selections
            Scope = Scope.root
        }

    (Eff.run env PromptSettings.read).GetAwaiter().GetResult()

[<Tests>]
let tests =
    testList
        "Settings"
        [
            test "the defaults" {
                Expect.equal (read []) PromptSettings.defaults "defaults"
                Expect.equal PromptSettings.defaults.Views [ "pins"; "status" ] "views"
                Expect.equal PromptSettings.defaults.Cache.Breakpoints Breakpoints.Rolling "rolling"
            }

            test "selections" {
                let settings =
                    read
                        [
                            "prompt.views", Json.Array [ Json.String "time"; Json.String "pins" ]
                            "prompt.cache.breakpoints", Json.String "prefix"
                            "prompt.cache.prefixTtl", Json.String "long"
                            "prompt.repoMap", Json.Bool true
                        ]

                Expect.equal settings.Views [ "time"; "pins" ] "views"
                Expect.equal settings.Cache.Breakpoints Breakpoints.Prefix "breakpoints"
                Expect.equal settings.Cache.PrefixTtl CacheTtl.Long "ttl"
                Expect.isTrue settings.RepoMap "repo map"

                let cfg =
                    PromptSettings.config settings [] Exclusions.none Support.breakpointCaching

                Expect.equal (cfg.Views |> List.map _.Id) [ "time"; "pins"; "notes" ] "notes last"
            }

            test "the slots check their values and join the core's registry" {
                let check (slot: SlotInfo) (json: Json) = slot.Check json

                let views = Slot.info PromptSlots.views

                Expect.isError (check views (Json.Array [ Json.String "nope" ])) "unknown view"

                Expect.isError (check views (Json.Array [ Json.String "pins"; Json.String "pins" ])) "twice"

                Expect.isOk (check views (Json.Array [])) "none"
                Expect.isError (check (Slot.info PromptSlots.breakpoints) (Json.String "all")) "not a choice"

                Expect.isOk (SlotRegistry.create (Slots.all @ PromptSlots.all)) "no clashes with the core's slots"
            }
        ]
