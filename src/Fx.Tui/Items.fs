namespace Fx.Tui

/// How much of each turn the transcript shows.
[<RequireQualifiedAccess>]
type Verbosity =
    /// The prompts, activity lines and result cards.
    | Quiet
    /// Also what the model wrote, and errors under the actions that failed.
    | Normal
    /// Also every cell's code and what each action returned.
    | Verbose

    override this.ToString() =
        match this with
        | Quiet -> "quiet"
        | Normal -> "normal"
        | Verbose -> "verbose"

/// An item of the transcript a person can select, and expand or collapse on its own: stable while
/// the transcript grows (a turn's items are only added to), so what was chosen for it stays.
[<RequireQualifiedAccess>]
type ItemId =
    /// An action (its activity line): the turn and the model's id for the tool call.
    | Call of turn: int * call: string
    /// What the model wrote: the turn's `index`th text (a draft counts).
    | Reply of turn: int * index: int
    /// A compaction: the turn's `index`th.
    | Compaction of turn: int * index: int
    /// The result card of a turn that ended.
    | Ending of turn: int

    override this.ToString() =
        match this with
        | Call(turn, call) -> $"call {turn}/{call}"
        | Reply(turn, index) -> $"reply {turn}/{index}"
        | Compaction(turn, index) -> $"compaction {turn}/{index}"
        | Ending turn -> $"ending {turn}"

/// How a person chose to show one item, whatever the detail (`Ctrl+O`) is.
[<RequireQualifiedAccess>]
type Fold =
    /// Its line alone (a reply: its first row).
    | Collapsed
    /// Everything it has: a cell's code, all an action returned, a compaction's summary, the
    /// evidence's details.
    | Expanded

    override this.ToString() =
        match this with
        | Collapsed -> "collapsed"
        | Expanded -> "expanded"

/// An item as the transcript shows it at a detail.
type Item =
    {
        Id: ItemId
        /// The detail alone shows more of it than its line (its first row).
        Open: bool
        /// The detail alone shows it (a reply is hidden when quiet).
        Shown: bool
    }

    override this.ToString() = string this.Id

/// The transcript's items, as the person can select them (spec §19.2: one item expanded or collapsed
/// on its own, the global detail under it).
[<RequireQualifiedAccess>]
module Items =
    /// The ids of a turn's items, one per item (`None`: an item that cannot be selected).
    let ids (turn: TurnView) : ItemId option list =
        let mutable replies = 0
        let mutable compactions = 0

        [
            for item in turn.Items do
                match item with
                | TurnItem.Call call -> Some(ItemId.Call(turn.Turn, call.Id))
                | TurnItem.Said _
                | TurnItem.Superseded _ ->
                    replies <- replies + 1
                    Some(ItemId.Reply(turn.Turn, replies - 1))
                | TurnItem.Compacted _ ->
                    compactions <- compactions + 1
                    Some(ItemId.Compaction(turn.Turn, compactions - 1))
                | _ -> None
        ]

    /// How a turn's item shows at a detail.
    let ofItem (verbosity: Verbosity) (id: ItemId) (item: TurnItem) : Item =
        let opened, shown =
            match item with
            | TurnItem.Said _ -> verbosity <> Verbosity.Quiet, verbosity <> Verbosity.Quiet
            | TurnItem.Superseded _ -> verbosity = Verbosity.Verbose, verbosity = Verbosity.Verbose
            | _ -> verbosity = Verbosity.Verbose, true

        {
            Id = id
            Open = opened
            Shown = shown
        }

    /// Whether an item shows: by the detail, unless a person chose how to show it.
    let shown (folds: Map<ItemId, Fold>) (item: Item) : bool = item.Shown || folds.ContainsKey item.Id

    /// Whether an item shows more than its line: as the person chose, else by the detail.
    let isOpen (folds: Map<ItemId, Fold>) (item: Item) : bool =
        match Map.tryFind item.Id folds with
        | Some Fold.Expanded -> true
        | Some Fold.Collapsed -> false
        | None -> item.Open

    /// The person's choice for an item turned over: shown open, it collapses; else it expands.
    let toggle (folds: Map<ItemId, Fold>) (item: Item) : Map<ItemId, Fold> =
        Map.add item.Id (if isOpen folds item then Fold.Collapsed else Fold.Expanded) folds

    /// The transcript's items in order, at a detail: every turn's actions, replies and compactions,
    /// then its result card.
    let all (verbosity: Verbosity) (blocks: Block list) : Item list =
        [
            for block in blocks do
                match block with
                | Block.Turn turn ->
                    for id, item in List.zip (ids turn) turn.Items do
                        match id with
                        | Some id -> ofItem verbosity id item
                        | None -> ()

                    // A result card shows whole at every detail (verbose adds the evidence's details).
                    if turn.Ended.IsSome then
                        {
                            Id = ItemId.Ending turn.Turn
                            Open = true
                            Shown = true
                        }
                | Block.Earlier _
                | Block.Note _ -> ()
        ]

    /// The items one can select now: those that show.
    let selectable (verbosity: Verbosity) (folds: Map<ItemId, Fold>) (blocks: Block list) : Item list =
        all verbosity blocks |> List.filter (shown folds)
