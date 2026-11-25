namespace Fx.Grants

/// An action grants are about: its name, the kind of its resources, and the scopes an answer may
/// choose. Extensions contribute their own (`GrantActions.contributionType` in Fx.Sdk).
type ActionDef =
    {
        Name: ActionName
        Resource: ResourceKind
        Description: string
        /// The scopes an answer may choose. `Once` is always allowed.
        Scopes: ScopeKind list
    }

    override this.ToString() =
        this.Name.Value + " (" + string this.Resource + ")"

/// The actions a grants log knows, by name. Checks and requests for other actions are refused, and
/// grants for them never apply.
type Actions =
    {
        Defs: Map<string, ActionDef>
    }

    override this.ToString() =
        "Actions(" + String.concat ", " (this.Defs |> Map.toList |> List.map fst) + ")"

[<RequireQualifiedAccess>]
module Actions =
    let private all =
        [ ScopeKind.Once; ScopeKind.Session; ScopeKind.Repo; ScopeKind.Host ]

    let private def (name: string) (kind: ResourceKind) (scopes: ScopeKind list) (description: string) : ActionDef =
        {
            Name = ActionName.create name
            Resource = kind
            Description = description
            Scopes = scopes
        }

    /// The core's vocabulary (spec §13.1 and the sections that name a grant).
    let builtInDefs: ActionDef list =
        [
            def "shell.exec" ResourceKind.Command all "Run a command (one simple command; split compound ones)."
            def "fs.read" ResourceKind.Path all "Read a file outside what a session reads freely."
            def "fs.write" ResourceKind.Path all "Write or delete a file."
            def "net.connect" ResourceKind.Name all "Open a network connection to `host:port` (spec §18 egress)."
            def
                "component.install"
                ResourceKind.Name
                [ ScopeKind.Once; ScopeKind.Repo; ScopeKind.Host ]
                "Install a component for the first time, `id@version` (spec §6.1)."
            def
                "budget.raise"
                ResourceKind.Name
                [ ScopeKind.Once ]
                "Raise a run's budget, `<run>/<budget>` (spec §8.2)."
            def "model.enable" ResourceKind.Name all "Enable a model, `<provider>/<model>` (spec §10.6)."
            def
                "model.paidFallback"
                ResourceKind.Name
                all
                "Fall back to a paid model, `<provider>/<model>` (spec §10.4)."
            def
                "provider.expose"
                ResourceKind.Name
                [ ScopeKind.Once; ScopeKind.Repo; ScopeKind.Host ]
                "Send content to a model provider for the first time (spec §13.1)."
            def "secret.read" ResourceKind.Name all "Use a secret by name (spec §11)."
            def "env.dotenv" ResourceKind.Path all "Load a project's `.env` file (spec §6.3)."
            def
                "env.cost"
                ResourceKind.Name
                [ ScopeKind.Once; ScopeKind.Session; ScopeKind.Host ]
                "Start an environment that costs money, `<provider>/<spec>` (spec §9)."
            def
                "safeOutput.apply"
                ResourceKind.Name
                [ ScopeKind.Once; ScopeKind.Session ]
                "Apply a run's safe output, `<type>/<target>` (spec §8.5)."
            def
                "workflow.approval"
                ResourceKind.Name
                [ ScopeKind.Once ]
                "Pass a workflow's approval gate, `<workflow>/<gate>` (spec §8.3)."
        ]

    /// Problems with action definitions: duplicates, a missing `Once`.
    let private check (defs: ActionDef list) : string list =
        let duplicates =
            defs
            |> List.countBy (fun d -> d.Name.Value)
            |> List.filter (fun (_, n) -> n > 1)
            |> List.map (fun (name, _) -> "two actions are named " + name)

        let noOnce =
            defs
            |> List.filter (fun d -> not (List.contains ScopeKind.Once d.Scopes))
            |> List.map (fun d -> d.Name.Value + " must allow answers for once")

        duplicates @ noOnce

    /// The actions, or what is wrong with them.
    let create (defs: ActionDef list) : Result<Actions, string list> =
        match check defs with
        | [] ->
            Ok
                {
                    Defs = defs |> List.map (fun d -> d.Name.Value, d) |> Map.ofList
                }
        | problems -> Error problems

    /// The core's actions.
    let builtIn: Actions =
        {
            Defs = builtInDefs |> List.map (fun d -> d.Name.Value, d) |> Map.ofList
        }

    /// The core's actions and these (contributed by extensions).
    let withContributed (contributed: ActionDef list) : Result<Actions, string list> =
        create (builtInDefs @ contributed)

    let tryFind (name: ActionName) (actions: Actions) : ActionDef option = Map.tryFind name.Value actions.Defs

    /// The resource kind of an action, if it is known.
    let kindOf (name: ActionName) (actions: Actions) : ResourceKind option =
        tryFind name actions |> Option.map (fun d -> d.Resource)
