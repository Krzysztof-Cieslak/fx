namespace Fx.Keyring.SecretService

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Fx.Effects
open Fx.Keyring
open Fx.Keyring.DBus

/// How secrets travel over the bus between this process and the Secret Service.
[<RequireQualifiedAccess>]
type SessionAlgorithm =
    /// Clear text (`plain`): every implementation supports it.
    | Plain
    /// `dh-ietf1024-sha256-aes128-cbc-pkcs7`: encrypted with a key from a Diffie-Hellman exchange.
    | DhAes

    member this.Name =
        match this with
        | Plain -> "plain"
        | DhAes -> SessionCrypto.Algorithm

    override this.ToString() = this.Name

type SecretServiceSettings =
    {
        /// The session bus address (`Headless.sessionBus`); `None` when there is no session bus.
        Bus: string option
        /// The algorithms to offer, in order; the first one the service supports is used.
        Algorithms: SessionAlgorithm list
        Prompts: Prompts
        /// Whether calls may start the service by D-Bus activation (e.g. gnome-keyring-daemon).
        AutoStart: bool
    }

    override this.ToString() =
        let algorithms = this.Algorithms |> List.map string |> String.concat ","
        $"SecretServiceSettings({algorithms}, prompts {this.Prompts}, auto-start {this.AutoStart})"

/// The Secret Service (https://specifications.freedesktop.org/secret-service-spec/latest/) over our
/// own D-Bus client: gnome-keyring, KeePassXC, KWallet 6 and others implement it.
///
/// Items are generic passwords in the default collection with the attributes `service` and
/// `account` (what `secret-tool lookup service <s> account <a>` finds). Every operation opens its
/// own connection and session, and closes them when done: keyring operations are rare, and nothing
/// stays open to be reconnected after the service restarts.
module SecretServiceKeyring =
    [<Literal>]
    let private Bus = "org.freedesktop.secrets"

    [<Literal>]
    let private ServicePath = "/org/freedesktop/secrets"

    [<Literal>]
    let private ServiceInterface = "org.freedesktop.Secret.Service"

    [<Literal>]
    let private CollectionInterface = "org.freedesktop.Secret.Collection"

    [<Literal>]
    let private ItemInterface = "org.freedesktop.Secret.Item"

    [<Literal>]
    let private PromptInterface = "org.freedesktop.Secret.Prompt"

    [<Literal>]
    let private PropertiesInterface = "org.freedesktop.DBus.Properties"

    /// No size limit is documented; this one keeps messages small. Tokens fit with room to spare.
    [<Literal>]
    let MaxValueBytes = 65536

    let defaults (bus: string option) (prompts: Prompts) : SecretServiceSettings =
        {
            Bus = bus
            Algorithms = [ SessionAlgorithm.DhAes; SessionAlgorithm.Plain ]
            Prompts = prompts
            AutoStart = true
        }

    let private utf8 = UTF8Encoding(false, true)

    /// A service call, without auto-start if the settings say so.
    let private call
        (settings: SecretServiceSettings)
        (connection: BusConnection)
        (path: string)
        (iface: string)
        (methodName: string)
        (body: DBusValue list)
        (cancellation: CancellationToken)
        =
        let message = Message.call Bus path iface methodName body

        let message =
            if settings.AutoStart then
                message
            else
                { message with
                    Flags = Message.NoAutoStart
                }

        connection.Call message cancellation

    let private toKeyringError (e: DBusError) : KeyringError =
        match e.Name with
        | "org.freedesktop.DBus.Error.ServiceUnknown"
        | "org.freedesktop.DBus.Error.NameHasNoOwner" -> KeyringError.Unavailable KeyringUnavailable.NoSecretService
        | name when name.StartsWith("org.freedesktop.DBus.Error.Spawn.", StringComparison.Ordinal) ->
            KeyringError.Unavailable KeyringUnavailable.NoSecretService
        | "org.freedesktop.Secret.Error.IsLocked" -> KeyringError.Locked
        | name -> KeyringError.Failed(name, e.Message)

    let private unexpected (what: string) (body: DBusValue list) : KeyringError =
        let signature = body |> List.map (fun v -> v.Type) |> String.concat ""
        KeyringError.Failed("org.freedesktop.DBus.Error.InvalidSignature", $"unexpected reply to {what} ({signature})")

    /// Calls a service method and reads its reply with `parse`.
    let private ask
        settings
        connection
        path
        iface
        methodName
        body
        (parse: DBusValue list -> 'T option)
        cancellation
        : Task<Result<'T, KeyringError>> =
        task {
            match! call settings connection path iface methodName body cancellation with
            | Error e -> return Error(toKeyringError e)
            | Ok reply ->
                match parse reply with
                | Some value -> return Ok value
                | None -> return Error(unexpected methodName reply)
        }

    let private property settings connection path iface name cancellation =
        ask
            settings
            connection
            path
            PropertiesInterface
            "Get"
            [ DBusValue.String iface; DBusValue.String name ]
            (fun reply ->
                match reply with
                | [ DBusValue.Variant value ] -> Some value
                | _ -> None)
            cancellation

    /// Runs a prompt (`Prompts.Allow`) and returns its result, or dismisses it (`Prompts.Never`).
    let private prompt
        (settings: SecretServiceSettings)
        (connection: BusConnection)
        (path: string)
        (cancellation: CancellationToken)
        : Task<Result<DBusValue, KeyringError>> =
        task {
            match settings.Prompts with
            | Prompts.Never ->
                let! _ = call settings connection path PromptInterface "Dismiss" [] cancellation
                return Error KeyringError.Locked
            | Prompts.Allow ->
                // Subscribe before prompting: Completed can follow Prompt's reply immediately.
                let rule =
                    $"type='signal',interface='{PromptInterface}',member='Completed',path='{path}'"

                let addMatch =
                    Message.call
                        "org.freedesktop.DBus"
                        "/org/freedesktop/DBus"
                        "org.freedesktop.DBus"
                        "AddMatch"
                        [ DBusValue.String rule ]

                match! connection.Call addMatch cancellation with
                | Error e -> return Error(KeyringError.Failed(e.Name, e.Message))
                | Ok _ ->
                    match!
                        call settings connection path PromptInterface "Prompt" [ DBusValue.String "" ] cancellation
                    with
                    | Error e -> return Error(toKeyringError e)
                    | Ok _ ->
                        let! completed =
                            connection.WaitForSignal
                                (fun m -> m.Path = Some path && m.Member = Some "Completed")
                                cancellation

                        match completed.Body with
                        | [ DBusValue.Boolean true; _ ] -> return Error KeyringError.Dismissed
                        | [ DBusValue.Boolean false; DBusValue.Variant result ] -> return Ok result
                        | body -> return Error(unexpected "Completed" body)
        }

    type private Session =
        {
            Path: string
            /// The AES key, or `None` for `plain`.
            Key: byte array option
        }

        override this.ToString() = $"Session({this.Path})"

    let rec private newKeyPair () =
        match SessionCrypto.KeyPair.FromRandom(RandomNumberGenerator.GetBytes SessionCrypto.KeyBytes) with
        | Some pair -> pair
        | None -> newKeyPair ()

    let private openSession settings connection cancellation : Task<Result<Session, KeyringError>> =
        task {
            let mutable result =
                Error(KeyringError.Failed("org.freedesktop.DBus.Error.NotSupported", "no session algorithm"))

            let mutable remaining = settings.Algorithms

            while not remaining.IsEmpty do
                let algorithm = remaining.Head
                remaining <- remaining.Tail

                let pair, input =
                    match algorithm with
                    | SessionAlgorithm.Plain -> None, DBusValue.String ""
                    | SessionAlgorithm.DhAes ->
                        let pair = newKeyPair ()
                        Some pair, DBusValue.Bytes pair.Public

                let! reply =
                    call
                        settings
                        connection
                        ServicePath
                        ServiceInterface
                        "OpenSession"
                        [ DBusValue.String algorithm.Name; DBusValue.Variant input ]
                        cancellation

                match reply, pair with
                | Ok [ DBusValue.Variant _; DBusValue.ObjectPath path ], None ->
                    result <- Ok { Path = path; Key = None }
                    remaining <- []
                | Ok [ DBusValue.Variant(DBusValue.Bytes peer); DBusValue.ObjectPath path ], Some pair ->
                    result <-
                        SessionCrypto.deriveKey pair peer
                        |> Result.map (fun key -> { Path = path; Key = Some key })
                        |> Result.mapError (fun e -> KeyringError.Failed("org.freedesktop.Secret.Error.NoSession", e))

                    remaining <- []
                | Ok body, _ ->
                    result <- Error(unexpected "OpenSession" body)
                    remaining <- []
                | Error e, _ when e.Name = "org.freedesktop.DBus.Error.NotSupported" ->
                    result <- Error(toKeyringError e)
                | Error e, _ ->
                    result <- Error(toKeyringError e)
                    remaining <- []

            return result
        }

    let private closeSession settings connection (session: Session) cancellation =
        task {
            let! _ =
                call settings connection session.Path "org.freedesktop.Secret.Session" "Close" [] cancellation

            return ()
        }

    let private objectPath (value: DBusValue) =
        match value with
        | DBusValue.ObjectPath path -> Some path
        | _ -> None

    /// The default collection, unlocked; created (with a prompt) if there is none and prompts are allowed.
    let private defaultCollection settings connection cancellation : Task<Result<string, KeyringError>> =
        task {
            let! alias =
                ask
                    settings
                    connection
                    ServicePath
                    ServiceInterface
                    "ReadAlias"
                    [ DBusValue.String "default" ]
                    (fun reply ->
                        match reply with
                        | [ DBusValue.ObjectPath path ] -> Some path
                        | _ -> None)
                    cancellation

            let! collection =
                match alias with
                | Ok "/" when settings.Prompts = Prompts.Allow ->
                    task {
                        let properties =
                            DBusValue.vardict [ CollectionInterface + ".Label", DBusValue.String "Default keyring" ]

                        match!
                            ask
                                settings
                                connection
                                ServicePath
                                ServiceInterface
                                "CreateCollection"
                                [ properties; DBusValue.String "default" ]
                                (fun reply ->
                                    match reply with
                                    | [ DBusValue.ObjectPath collection; DBusValue.ObjectPath prompt ] ->
                                        Some(collection, prompt)
                                    | _ -> None)
                                cancellation
                        with
                        | Error e -> return Error e
                        | Ok(collection, "/") -> return Ok collection
                        | Ok(_, promptPath) ->
                            match! prompt settings connection promptPath cancellation with
                            | Error e -> return Error e
                            | Ok result ->
                                match objectPath result with
                                | Some path -> return Ok path
                                | None -> return Error(unexpected "CreateCollection" [ result ])
                    }
                | Ok "/" -> Task.FromResult(Error(KeyringError.Unavailable KeyringUnavailable.NoDefaultCollection))
                | other -> Task.FromResult other

            match collection with
            | Error e -> return Error e
            | Ok path ->
                match! property settings connection path CollectionInterface "Locked" cancellation with
                | Error e -> return Error e
                | Ok(DBusValue.Boolean false) -> return Ok path
                | Ok(DBusValue.Boolean true) when settings.Prompts = Prompts.Never -> return Error KeyringError.Locked
                | Ok(DBusValue.Boolean true) ->
                    match!
                        ask
                            settings
                            connection
                            ServicePath
                            ServiceInterface
                            "Unlock"
                            [ DBusValue.paths [ path ] ]
                            (fun reply ->
                                match reply with
                                | [ DBusValue.Array("o", unlocked); DBusValue.ObjectPath prompt ] ->
                                    Some(List.choose objectPath unlocked, prompt)
                                | _ -> None)
                            cancellation
                    with
                    | Error e -> return Error e
                    | Ok(unlocked, _) when List.contains path unlocked -> return Ok path
                    | Ok(_, "/") -> return Error KeyringError.Locked
                    | Ok(_, promptPath) ->
                        let! outcome = prompt settings connection promptPath cancellation
                        return outcome |> Result.map (fun _ -> path)
                | Ok other -> return Error(unexpected "Locked" [ other ])
        }

    let private attributes (key: KeyringKey) =
        [ "service", key.Service.Name; "account", key.Account ]

    let private search settings connection collection (attributes: (string * string) list) cancellation =
        ask
            settings
            connection
            collection
            CollectionInterface
            "SearchItems"
            [ DBusValue.stringDict attributes ]
            (fun reply ->
                match reply with
                | [ DBusValue.Array("o", items) ] ->
                    Some(
                        items
                        |> List.choose objectPath
                        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
                    )
                | _ -> None)
            cancellation

    /// Connects, opens a session and the default collection, runs `body`, and closes everything.
    let private run
        (settings: SecretServiceSettings)
        (body: BusConnection -> Session -> string -> Task<Result<'T, KeyringError>>)
        (cancellation: CancellationToken)
        : Task<Result<'T, KeyringError>> =
        task {
            match settings.Bus with
            | None -> return Error(KeyringError.Unavailable KeyringUnavailable.NoSessionBus)
            | Some address ->
                match! BusConnection.Connect address cancellation with
                | Error reason -> return Error(KeyringError.Unavailable(KeyringUnavailable.BusUnreachable reason))
                | Ok connection ->
                    use connection = connection

                    try
                        match! openSession settings connection cancellation with
                        | Error e -> return Error e
                        | Ok session ->
                            let! result =
                                task {
                                    match! defaultCollection settings connection cancellation with
                                    | Error e -> return Error e
                                    | Ok collection -> return! body connection session collection
                                }

                            do! closeSession settings connection session cancellation
                            return result
                    with :? IOException as e ->
                        return Error(KeyringError.Failed("org.freedesktop.DBus.Error.Disconnected", e.Message))
        }

    let private secretOf (session: Session) (value: string) : DBusValue =
        let plaintext = utf8.GetBytes value

        let parameters, bytes =
            match session.Key with
            | None -> Array.empty, plaintext
            | Some key -> SessionCrypto.encrypt key (RandomNumberGenerator.GetBytes 16) plaintext

        DBusValue.Struct
            [
                DBusValue.ObjectPath session.Path
                DBusValue.Bytes parameters
                DBusValue.Bytes bytes
                DBusValue.String "text/plain"
            ]

    let private textOf (session: Session) (secret: DBusValue) : Result<string, KeyringError> =
        match secret with
        | DBusValue.Struct [ DBusValue.ObjectPath _
                             DBusValue.Bytes parameters
                             DBusValue.Bytes value
                             DBusValue.String _ ] ->
            let plaintext =
                match session.Key with
                | None -> Ok value
                | Some key ->
                    SessionCrypto.decrypt key parameters value
                    |> Result.mapError (fun e -> KeyringError.Failed("org.freedesktop.Secret.Error.NoSession", e))

            plaintext
            |> Result.bind (fun bytes ->
                try
                    Ok(utf8.GetString bytes)
                with :? DecoderFallbackException ->
                    Error(KeyringError.Failed("encoding", "the secret is not UTF-8 text")))
        | other -> Error(unexpected "GetSecret" [ other ])

    /// Waits for a prompt an operation returned ("/" means none).
    let private settle settings connection (promptPath: string) cancellation : Task<Result<unit, KeyringError>> =
        if promptPath = "/" then
            Task.FromResult(Ok())
        else
            task {
                let! outcome = prompt settings connection promptPath cancellation
                return outcome |> Result.map ignore
            }

    let private read settings (key: KeyringKey) cancellation =
        run
            settings
            (fun connection session collection ->
                task {
                    match! search settings connection collection (attributes key) cancellation with
                    | Error e -> return Error e
                    | Ok [] -> return Ok None
                    | Ok(item :: _) ->
                        let! secret =
                            ask
                                settings
                                connection
                                item
                                ItemInterface
                                "GetSecret"
                                [ DBusValue.ObjectPath session.Path ]
                                (fun reply ->
                                    match reply with
                                    | [ secret ] -> Some secret
                                    | _ -> None)
                                cancellation

                        return
                            secret
                            |> Result.bind (textOf session)
                            |> Result.map (fun text -> Some(SecretText text))
                })
            cancellation

    let private write settings (key: KeyringKey) (value: SecretText) cancellation =
        if Keyring.byteCount value > MaxValueBytes then
            Task.FromResult(Error(KeyringError.TooLarge MaxValueBytes))
        else
            run
                settings
                (fun connection session collection ->
                    task {
                        let properties =
                            DBusValue.vardict
                                [
                                    ItemInterface + ".Label", DBusValue.String $"{key.Service}: {key.Account}"
                                    ItemInterface + ".Attributes",
                                    DBusValue.stringDict (
                                        ("xdg:schema", "org.freedesktop.Secret.Generic") :: attributes key
                                    )
                                ]

                        match!
                            ask
                                settings
                                connection
                                collection
                                CollectionInterface
                                "CreateItem"
                                [ properties; secretOf session (value.Reveal()); DBusValue.Boolean true ]
                                (fun reply ->
                                    match reply with
                                    | [ DBusValue.ObjectPath item; DBusValue.ObjectPath prompt ] -> Some(item, prompt)
                                    | _ -> None)
                                cancellation
                        with
                        | Error e -> return Error e
                        | Ok(_, promptPath) -> return! settle settings connection promptPath cancellation
                    })
                cancellation

    let private delete settings (key: KeyringKey) cancellation =
        run
            settings
            (fun connection _ collection ->
                task {
                    match! search settings connection collection (attributes key) cancellation with
                    | Error e -> return Error e
                    | Ok items ->
                        let mutable result = Ok(not items.IsEmpty)

                        for item in items do
                            if Result.isOk result then
                                match!
                                    ask
                                        settings
                                        connection
                                        item
                                        ItemInterface
                                        "Delete"
                                        []
                                        (fun reply ->
                                            match reply with
                                            | [ DBusValue.ObjectPath prompt ] -> Some prompt
                                            | _ -> None)
                                        cancellation
                                with
                                | Error e -> result <- Error e
                                | Ok promptPath ->
                                    match! settle settings connection promptPath cancellation with
                                    | Error e -> result <- Error e
                                    | Ok() -> ()

                        return result
                })
            cancellation

    let private list settings (service: KeyringService) cancellation =
        run
            settings
            (fun connection _ collection ->
                task {
                    match! search settings connection collection [ "service", service.Name ] cancellation with
                    | Error e -> return Error e
                    | Ok items ->
                        let accounts = ResizeArray()
                        let mutable failure = None

                        for item in items do
                            if failure.IsNone then
                                match! property settings connection item ItemInterface "Attributes" cancellation with
                                | Error e -> failure <- Some e
                                | Ok(DBusValue.Array(_, entries)) ->
                                    for entry in entries do
                                        match entry with
                                        | DBusValue.DictEntry(DBusValue.String "account", DBusValue.String account) ->
                                            accounts.Add account
                                        | _ -> ()
                                | Ok other -> failure <- Some(unexpected "Attributes" [ other ])

                        match failure with
                        | Some e -> return Error e
                        | None ->
                            return
                                Ok(
                                    accounts
                                    |> Seq.distinct
                                    |> Seq.sortWith (fun a b -> String.CompareOrdinal(a, b))
                                    |> List.ofSeq
                                )
                })
            cancellation

    /// Whether the service is there and its default collection unlocked, without prompting or
    /// creating anything (it may start the service if `AutoStart`).
    let probe (settings: SecretServiceSettings) (cancellation: CancellationToken) : Task<KeyringStatus> =
        task {
            match settings.Bus with
            | None -> return KeyringStatus.Unavailable KeyringUnavailable.NoSessionBus
            | Some address ->
                match! BusConnection.Connect address cancellation with
                | Error reason -> return KeyringStatus.Unavailable(KeyringUnavailable.BusUnreachable reason)
                | Ok connection ->
                    use connection = connection

                    try
                        let! alias =
                            ask
                                settings
                                connection
                                ServicePath
                                ServiceInterface
                                "ReadAlias"
                                [ DBusValue.String "default" ]
                                (fun reply ->
                                    match reply with
                                    | [ DBusValue.ObjectPath path ] -> Some path
                                    | _ -> None)
                                cancellation

                        match alias with
                        | Error(KeyringError.Unavailable reason) -> return KeyringStatus.Unavailable reason
                        | Error(KeyringError.Failed(code, message)) -> return KeyringStatus.Failed(code, message)
                        | Error e -> return KeyringStatus.Failed("unexpected", string e)
                        | Ok "/" -> return KeyringStatus.Unavailable KeyringUnavailable.NoDefaultCollection
                        | Ok path ->
                            match! property settings connection path CollectionInterface "Locked" cancellation with
                            | Ok(DBusValue.Boolean false) -> return KeyringStatus.Ready
                            | Ok(DBusValue.Boolean true) -> return KeyringStatus.Locked
                            | Ok other -> return KeyringStatus.Failed("unexpected", $"Locked is {other}")
                            | Error e -> return KeyringStatus.Failed("unexpected", string e)
                    with :? IOException as e ->
                        return KeyringStatus.Failed("org.freedesktop.DBus.Error.Disconnected", e.Message)
        }

    let create (settings: SecretServiceSettings) : Keyring =
        {
            Backend = "secret-service"
            MaxValueBytes = MaxValueBytes
            Probe = probe settings
            Read = read settings
            Write = write settings
            Delete = delete settings
            List = list settings
        }
