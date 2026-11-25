namespace Fx.Prompt

open Fx.Contracts
open Fx.Effects
open Fx.Models
open Fx.Variants

/// The render's variant slots (spec §15: render, previews, repo map). They are core slots defined next
/// to the code that reads them: the host registers `PromptSlots.all` with `Slots.all`.
[<RequireQualifiedAccess>]
module PromptSlots =
    /// The view ids `prompt.views` may list.
    let viewIds = Views.builtIn |> List.map _.Id

    /// `prompt.views`: the suffix's views, in order (`notes` always comes last).
    let views: Slot<string list> =
        Slot.create
            "prompt.views"
            "The views at the end of every prompt, in order: pins (pinned values and live files), status (budget and other status lines), time (today's date)."
            (Codec.list (Codec.enum (viewIds |> List.map (fun id -> id, id)))
             |> Codec.refine (fun ids ->
                 if List.distinct ids = ids then
                     Ok()
                 else
                     Error "each view at most once"))
            [ "pins"; "status" ]

    /// `prompt.cache.breakpoints`: `off`, `prefix` or `rolling`.
    let breakpoints: Slot<Breakpoints> =
        Slot.enum
            "prompt.cache.breakpoints"
            "Where cache breakpoints go: off, prefix (after the stable prefix), or rolling (the prefix, the end of each request and the latest earlier ends)."
            [
                Breakpoints.Off, "off"
                Breakpoints.Prefix, "prefix"
                Breakpoints.Rolling, "rolling"
            ]
            Breakpoints.Rolling

    /// `prompt.cache.prefixTtl`: `short` or `long`.
    let prefixTtl: Slot<CacheTtl> =
        Slot.enum
            "prompt.cache.prefixTtl"
            "How long the cached prefix should live where the model offers a choice: short (minutes) or long (about an hour)."
            [ CacheTtl.Short, "short"; CacheTtl.Long, "long" ]
            CacheTtl.Short

    /// `prompt.repoMap`: whether the prefix has a repo map (spec §6.7).
    let repoMap: Slot<bool> =
        Slot.create "prompt.repoMap" "Whether the prompt's prefix includes a map of the repository." Codec.bool false
        |> Slot.withChoices [ false; true ]

    /// The render's slots, for the host's registry.
    let all: SlotInfo list =
        [
            Slot.info views
            Slot.info breakpoints
            Slot.info prefixTtl
            Slot.info repoMap
        ]

/// The render's configurable behaviour, read from variants.
type PromptSettings =
    {
        Views: string list
        Cache: CachePolicy
        RepoMap: bool
        /// Bounds of the previews receipts show (`values.previewLimits`).
        Limits: Limits
    }

    override this.ToString() =
        let views = String.concat "," this.Views
        $"prompt settings (views {views}, {this.Cache}, repo map {this.RepoMap})"

[<RequireQualifiedAccess>]
module PromptSettings =
    /// Every slot at its default.
    let defaults: PromptSettings =
        {
            Views = PromptSlots.views.Default
            Cache =
                {
                    Breakpoints = PromptSlots.breakpoints.Default
                    PrefixTtl = PromptSlots.prefixTtl.Default
                }
            RepoMap = PromptSlots.repoMap.Default
            Limits = Slots.previewLimits.Default
        }

    /// Reads the settings through `IVariants` (recorded and replayable like every read).
    let read<'R when 'R :> IVariants> : Eff<'R, PromptSettings> =
        eff {
            let! views = Variants.get PromptSlots.views
            let! breakpoints = Variants.get PromptSlots.breakpoints
            let! prefixTtl = Variants.get PromptSlots.prefixTtl
            let! repoMap = Variants.get PromptSlots.repoMap
            let! limits = Variants.get Slots.previewLimits

            return
                {
                    Views = views
                    Cache =
                        {
                            Breakpoints = breakpoints
                            PrefixTtl = prefixTtl
                        }
                    RepoMap = repoMap
                    Limits = limits
                }
        }

    /// A render configuration from the settings, the prefix's sections, the content exclusions and the
    /// model's caching.
    let config
        (settings: PromptSettings)
        (sections: Block list)
        (exclusions: Exclusions)
        (caching: CachingCaps)
        : RenderConfig =
        {
            Sections = sections
            Views = Views.select settings.Views
            Exclusions = exclusions
            Cache = settings.Cache
            Caching = caching
        }
