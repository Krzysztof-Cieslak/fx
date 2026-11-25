namespace Fx.Auth

open Fx.Sdk

/// Which credential store the host uses, and why (docs/spikes/keychains.md, "Headless behaviour").
type StoreChoice =
    {
        /// The store's kind.
        Kind: string
        /// Why: the setting named it, or what `auto` found.
        Reason: string
        /// What to tell the person once (`auth status`, and with the first credential stored): a
        /// downgrade from the keyring to a file, or a keyring nobody can unlock here.
        Notice: string option
    }

    override this.ToString() = this.Kind + " (" + this.Reason + ")"

[<RequireQualifiedAccess>]
module StoreChoice =
    /// The OS keyring's kind, which `auto` prefers.
    [<Literal>]
    let Keyring = "keyring"

    /// The file store's kind, which `auto` falls back to (saying so).
    [<Literal>]
    let File = "file"

    let private names (statuses: (string * StoreStatus) list) =
        match statuses with
        | [] -> "none"
        | statuses -> statuses |> List.map fst |> String.concat ", "

    /// Picks the store from `credentials.store` and what each contributed store's probe said, once,
    /// when the host starts. `prompts`: whether the keyring may ask the person (a local, interactive
    /// session). Pure.
    ///
    /// - A named store is used if it can be: one that is unavailable is an error naming the reason
    ///   and the fix, never a fallback. A locked one is used (unlocking it is the fix).
    /// - `auto`: the keyring if it is ready, or locked (it asks to unlock, or says how); otherwise the
    ///   file store, with a notice saying why. `ephemeral` is never picked: it loses credentials.
    let choose (setting: string) (statuses: (string * StoreStatus) list) (prompts: bool) : Result<StoreChoice, string> =
        let find kind =
            statuses |> List.tryFind (fun (k, _) -> k = kind) |> Option.map snd

        let lockedNotice (kind: string) (hint: string) =
            if prompts then
                None
            else
                Some(
                    "the "
                    + kind
                    + " store is locked and nobody can be asked to unlock it here: "
                    + hint
                )

        if setting = AuthSlots.Auto then
            let fallback (why: string) =
                match find File with
                | Some StoreStatus.Ready ->
                    Ok
                        {
                            Kind = File
                            Reason = "auto: " + why
                            Notice =
                                Some(
                                    "credentials are kept in a file only you can read, because the OS keyring cannot be used: "
                                    + why
                                )
                        }
                | Some(StoreStatus.Unavailable reason) ->
                    Error(
                        "no credential store can be used: the OS keyring: "
                        + why
                        + "; the file: "
                        + reason
                    )
                | Some(StoreStatus.Locked hint) ->
                    Error("no credential store can be used: the file store is locked: " + hint)
                | None ->
                    Error(
                        "no credential store can be used: the OS keyring: "
                        + why
                        + "; no file store (stores: "
                        + names statuses
                        + ")"
                    )

            match find Keyring with
            | Some StoreStatus.Ready ->
                Ok
                    {
                        Kind = Keyring
                        Reason = "auto: the OS keyring is ready"
                        Notice = None
                    }
            | Some(StoreStatus.Locked hint) ->
                Ok
                    {
                        Kind = Keyring
                        Reason =
                            if prompts then
                                "auto: the OS keyring is locked; its first use asks to unlock it"
                            else
                                "auto: the OS keyring is locked"
                        Notice = lockedNotice Keyring hint
                    }
            | Some(StoreStatus.Unavailable reason) -> fallback reason
            | None -> fallback "no keyring store is enabled"
        else
            match find setting with
            | None ->
                Error(
                    "credentials.store is "
                    + setting
                    + ", but no enabled extension provides that store (stores: "
                    + names statuses
                    + ")"
                )
            | Some(StoreStatus.Unavailable reason) ->
                Error(
                    "credentials.store is "
                    + setting
                    + ", but that store cannot be used here: "
                    + reason
                )
            | Some(StoreStatus.Locked hint) ->
                Ok
                    {
                        Kind = setting
                        Reason = "credentials.store"
                        Notice = lockedNotice setting hint
                    }
            | Some StoreStatus.Ready ->
                Ok
                    {
                        Kind = setting
                        Reason = "credentials.store"
                        Notice = None
                    }
