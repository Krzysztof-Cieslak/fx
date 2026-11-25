namespace Fx.Sdk

open System
open Fx.Contracts
open Fx.Effects
open Fx.Models
open Fx.Variants

/// What content exclusions do with excluded content in a model request.
[<RequireQualifiedAccess>]
type ExclusionAction =
    /// Replace it with a placeholder; the gate denies what is left.
    | Redact
    /// Change nothing; the gate denies any request that holds excluded content.
    | Deny

    override this.ToString() =
        match this with
        | Redact -> "redact"
        | Deny -> "deny"

/// The content exclusions in effect for a request: the patterns of every origin, what the
/// placeholder says, and what to do with excluded content.
type ExclusionSettings =
    {
        /// One list per origin; a path is excluded if any list excludes it.
        Lists: ExclusionList list
        Placeholder: PlaceholderStyle
        Action: ExclusionAction
    }

    override this.ToString() =
        $"ExclusionSettings({this.Lists.Length} lists, {this.Placeholder}, {this.Action})"

/// Content exclusions (spec §5, §6.5, §18; docs/modules/egress.md): their configuration as variant
/// slots, and the model-egress hooks that keep excluded content out of model requests. The
/// built-in `fx.egress.exclusions` contributes these; providers with policies of their own (Copilot
/// organisation exclusions, T210) build their own hooks with `hooks`, which can only exclude more.
[<RequireQualifiedAccess>]
module ContentExclusions =
    /// The origins of the lists the slots hold, in the order they are tried.
    [<Literal>]
    let PolicyOrigin = "policy"

    [<Literal>]
    let HostOrigin = "host"

    [<Literal>]
    let ProjectOrigin = "project"

    /// Patterns from policy (pinned by policy files): privileged, so neither a project nor a session
    /// can change them.
    let policyPatterns: Slot<string list> =
        Slot.create
            "egress.exclusions.policy"
            "Files whose content never goes to models, set by policy (gitignore syntax, relative to the workspace)."
            ExclusionList.patterns
            []
        |> Slot.privileged

    /// Patterns from the host's config: privileged.
    let hostPatterns: Slot<string list> =
        Slot.create
            "egress.exclusions.host"
            "Files whose content never goes to models, from the host's config (gitignore syntax, relative to the workspace)."
            ExclusionList.patterns
            []
        |> Slot.privileged

    /// Patterns from the trusted project's config. They add to the others; none can remove another's.
    let projectPatterns: Slot<string list> =
        Slot.create
            "egress.exclusions.project"
            "Files whose content never goes to models, from the project's config (gitignore syntax, relative to the workspace)."
            ExclusionList.patterns
            []

    /// What a placeholder says: `plain` (only that content was excluded) or `path` (also the file).
    let placeholder: Slot<PlaceholderStyle> =
        Slot.enum
            "egress.exclusions.placeholder"
            "What replaces excluded content: plain (only that it was excluded) or path (also which file). Never sizes or digests."
            [ PlaceholderStyle.Plain, "plain"; PlaceholderStyle.WithPath, "path" ]
            PlaceholderStyle.Plain

    /// `redact` (replace excluded content, deny what is left) or `deny` (deny requests that hold it).
    let action: Slot<ExclusionAction> =
        Slot.enum
            "egress.exclusions.action"
            "What happens to excluded content in a model request: redact (replaced with a placeholder) or deny (the request is not sent)."
            [ ExclusionAction.Redact, "redact"; ExclusionAction.Deny, "deny" ]
            ExclusionAction.Redact

    /// The slots, for the contributing extension (`VariantSlots.contributionType`, ids as given).
    let slots: SlotInfo list =
        [
            Slot.info policyPatterns
            Slot.info hostPatterns
            Slot.info projectPatterns
            Slot.info placeholder
            Slot.info action
        ]

    /// No exclusions.
    let none: ExclusionSettings =
        {
            Lists = []
            Placeholder = PlaceholderStyle.Plain
            Action = ExclusionAction.Redact
        }

    let private orDefault (variants: Variants) (slot: Slot<'T>) : 'T =
        match variants.Read slot.Id with
        | None -> slot.Default
        | Some read ->
            match slot.Codec.Decode read.Value with
            | Ok value -> value
            | Error issues -> raise (SlotTypeMismatchException(slot.Id, issues))

    /// The settings from variants: policy, host, then project patterns. Slots that are not
    /// registered (the extension is disabled) read as their defaults: no exclusions.
    let read<'R when 'R :> IVariants> : Eff<'R, ExclusionSettings> =
        fun env ->
            let get slot = orDefault env.Variants slot

            Threading.Tasks.Task.FromResult
                {
                    Lists =
                        [
                            {
                                Origin = PolicyOrigin
                                Patterns = get policyPatterns
                            }
                            {
                                Origin = HostOrigin
                                Patterns = get hostPatterns
                            }
                            {
                                Origin = ProjectOrigin
                                Patterns = get projectPatterns
                            }
                        ]
                        |> List.filter (fun l -> not l.Patterns.IsEmpty)
                    Placeholder = get placeholder
                    Action = get action
                }

    type private VariantsEnv =
        {
            Variants: Variants
        }

        interface IVariants with
            member env.Variants = env.Variants

    /// The settings from a variants interpreter (the resolution in effect), whatever the caller's
    /// environment.
    let ofVariants<'R> (variants: Variants) : Eff<'R, ExclusionSettings> = fun _ -> read { Variants = variants }

    /// How an environment's paths compare: case-sensitive on Linux, insensitive on Windows and macOS
    /// (their default filesystems).
    let caseOf (os: Os) : PathCase =
        match os with
        | Os.Linux -> PathCase.Sensitive
        | Os.MacOs
        | Os.Windows -> PathCase.Insensitive

    /// The compiled exclusions of settings. The model-egress hooks compare without case (the paths
    /// may come from any environment, and excluding too much is the safe side).
    let compile (case: PathCase) (settings: ExclusionSettings) : Validation<Exclusions, Issue> =
        Exclusions.create case settings.Lists

    /// The priority of the exclusions' transform: before every other egress transform, so the others
    /// (redaction, T601) see placeholders, never excluded content.
    [<Literal>]
    let Priority = -1000

    /// Compiles settings, keeping the last compilation (settings rarely change between requests).
    type private Compiler() =
        let gate = obj ()
        let mutable last: (ExclusionSettings * Validation<Exclusions, Issue>) option = None

        member _.Compile(settings: ExclusionSettings) : Validation<Exclusions, Issue> =
            lock gate (fun () ->
                match last with
                | Some(previous, compiled) when previous = settings -> compiled
                | _ ->
                    let compiled = compile PathCase.Insensitive settings
                    last <- Some(settings, compiled)
                    compiled)

    /// Raised by the transform when the settings do not compile: the pipeline fails, nothing is sent.
    exception InvalidExclusionsException of issues: Issue list

    let private describe (found: ExcludedContent list) : string =
        match found with
        | [] -> ""
        | first :: rest ->
            let more = if rest.IsEmpty then "" else $" (and {rest.Length} more)"

            $"content of {first.Match.Path}, excluded by {first.Match.Origin} ({first.Match.Pattern}), would leave in {first.Location}{more}"

    /// The model-egress hooks of settings read per request (`settings` runs in the hook's extension
    /// environment): a transform at `Priority` that replaces excluded parts with placeholders (when
    /// the action is `Redact`), and a gate that denies requests still holding excluded content (or
    /// whose settings do not compile). Content is matched by its parts' sources
    /// (`ContentSource`); parts without sources are not touched.
    let hooks (settings: Eff<IExtensionEnv, ExclusionSettings>) : Transform<ChatRequest> * Gate<ChatRequest> =
        let compiler = Compiler()

        let transform: Transform<ChatRequest> =
            {
                Priority = Priority
                Apply =
                    fun request ->
                        eff {
                            let! settings = settings

                            match settings.Action, compiler.Compile settings with
                            | _, Error issues -> return raise (InvalidExclusionsException issues)
                            | ExclusionAction.Deny, Ok _ -> return request
                            | ExclusionAction.Redact, Ok exclusions ->
                                let redacted, _ =
                                    ExclusionRedaction.redact (Placeholder.text settings.Placeholder) exclusions request

                                return redacted
                        }
            }

        let gate: Gate<ChatRequest> =
            {
                Check =
                    fun request ->
                        eff {
                            let! settings = settings

                            match compiler.Compile settings with
                            | Error issues ->
                                return
                                    Decision.Deny(
                                        "the content exclusions are invalid: "
                                        + String.Join("; ", issues |> List.map string)
                                    )
                            | Ok exclusions ->
                                match ExclusionRedaction.find exclusions request with
                                | [] -> return Decision.Allow
                                | found -> return Decision.Deny(describe found)
                        }
            }

        transform, gate
