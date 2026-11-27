namespace Fx.Environments

open System
open System.Text
open Fx.Contracts
open Fx.Sdk
open Fx.Variants

/// Where a variable's value comes from (spec §9.4 `variables`, the `env.variables` variant).
[<RequireQualifiedAccess>]
type VariableSource =
    /// The host's own variable of this name (`host:<NAME>`).
    | Host of name: string
    /// An fx secret, resolved by the host's secret sources (`secret:<name>`).
    | Secret of name: string
    /// The value as written: only for names that do not look like credentials.
    | Literal of value: string

    /// The source as config and records name it: the reference, never a literal's value.
    member this.Reference =
        match this with
        | Host name -> "host:" + name
        | Secret name -> "secret:" + name
        | Literal _ -> "literal"

    override this.ToString() = this.Reference

/// Who asked for a variable: what decides whether it needs a person's consent.
[<RequireQualifiedAccess>]
type VariableOrigin =
    /// The person's own settings: the privileged variant `env.variables` (host config or the
    /// command line). Applies without asking.
    | Person
    /// An environment spec from the host's config: the person's file too. Applies without asking.
    | HostSpec
    /// An environment spec from the project's (trusted) config: its credentials need consent.
    | ProjectSpec

    override this.ToString() =
        match this with
        | Person -> "person"
        | HostSpec -> "host"
        | ProjectSpec -> "project"

/// What became of a requested variable.
[<RequireQualifiedAccess>]
type VariableOutcome =
    /// Passed to the environment's commands without asking (the person chose it, or it is plain).
    | Passed
    /// Passed with a person's consent: the grant that allowed it.
    | Granted of grant: string
    /// Left out: a person denied it, nobody could answer, or the grants log could not be asked.
    | Denied of reason: string
    /// Left out: no value (the host variable is not set, the secret is missing, the value is too
    /// short to redact).
    | Unavailable of reason: string

    override this.ToString() =
        match this with
        | Passed -> "passed"
        | Granted grant -> "granted (" + grant + ")"
        | Denied reason -> "denied: " + reason
        | Unavailable reason -> "unavailable: " + reason

/// One variable an environment was asked to get, as the host records it: by name and source, never
/// by value (spec §18).
type EnvVariable =
    {
        Name: string
        /// `host:<NAME>`, `secret:<name>` or `literal`.
        Source: string
        Origin: VariableOrigin
        /// Whether it is a credential (its name looks like one, or it is a secret): redacted from
        /// everything the environment hands the host's turns, and a project needs consent for it.
        Credential: bool
        Outcome: VariableOutcome
    }

    /// Whether the environment's commands get it.
    member this.Passed =
        match this.Outcome with
        | VariableOutcome.Passed
        | VariableOutcome.Granted _ -> true
        | _ -> false

    override this.ToString() =
        $"{this.Name} from {this.Source} ({this.Origin}): {this.Outcome}"

/// A variable the host is to resolve for an environment: what was asked, by whom, and whether it
/// needs a person's consent.
type VariableRequest =
    {
        Name: string
        Source: VariableSource
        Origin: VariableOrigin
        Credential: bool
        /// A project's credential: allowed only by a grant (`env.credential`, `secret.read`).
        NeedsConsent: bool
    }

    override this.ToString() =
        $"{this.Name} from {this.Source} ({this.Origin})"

/// What the host resolved for one start of an environment: the values its commands get, the values to
/// redact, and the record. The values are never persisted.
[<NoEquality; NoComparison>]
type ResolvedVariables =
    {
        /// Name and value of every variable passed, in order.
        Values: (string * string) list
        /// Name and value of every credential passed: what is redacted.
        Secrets: (string * string) list
        Record: EnvVariable list
    }

    override this.ToString() =
        $"{this.Values.Length} variables ({this.Secrets.Length} credentials)"

/// The variables a person chooses for environments, and the requests of environment specs (spec §9.4,
/// §18; docs/modules/environments.md, "Variables").
[<RequireQualifiedAccess>]
module EnvVariables =
    /// The shortest credential value passed: a shorter one would be found all over ordinary output,
    /// so redacting it would mangle that output.
    [<Literal>]
    let MinSecretLength = 8

    /// No variables.
    let none: ResolvedVariables =
        {
            Values = []
            Secrets = []
            Record = []
        }

    /// A value as config writes it: `host:<NAME>`, `secret:<name>` (prefixes without case), or a literal.
    let parse (text: string) : VariableSource =
        if text.StartsWith("host:", StringComparison.OrdinalIgnoreCase) then
            VariableSource.Host(text.Substring 5)
        elif text.StartsWith("secret:", StringComparison.OrdinalIgnoreCase) then
            VariableSource.Secret(text.Substring 7)
        else
            VariableSource.Literal text

    /// Whether a variable is a credential: its name, or the host variable it reads, looks like one, or
    /// its value is a secret.
    let isCredential (name: string) (source: VariableSource) : bool =
        HostEnvironment.isCredential name
        || (match source with
            | VariableSource.Secret _ -> true
            | VariableSource.Host other -> HostEnvironment.isCredential other
            | VariableSource.Literal _ -> false)

    let private validName (name: string) =
        name <> ""
        && name
           |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '_' || c = '.' || c = '-')
        && not (Char.IsAsciiDigit name[0])

    /// Every reason a map of variables cannot be used, with paths to the names: a name that is not a
    /// variable's, a runtime injection vector (`NODE_OPTIONS`, …), a credential written as a literal,
    /// a reference without a name. Empty when it is fine.
    let check (variables: Map<string, string>) : Issue list =
        [
            for KeyValue(name, value) in variables do
                let at (message: string) =
                    Issue.at [ PathSegment.Key name ] message

                if not (validName name) then
                    at
                        $"{Json.quote name}: expected a variable's name (letters, digits, \"_\", \".\" and \"-\", not starting with a digit)"
                elif InjectionVectors.isVector name then
                    at $"{name} is a runtime injection vector: never passed to an environment (spec §18)"
                else
                    match parse value with
                    | VariableSource.Host ""
                    | VariableSource.Secret "" -> at $"{Json.quote value}: name the variable or secret after the prefix"
                    | VariableSource.Host other when not (validName other) ->
                        at $"{Json.quote other}: expected a host variable's name"
                    | VariableSource.Secret secret ->
                        match Fx.Effects.SecretRef.tryCreate secret with
                        | Ok _ -> ()
                        | Error e -> at $"{Json.quote secret}: {e}"
                    | VariableSource.Literal _ when HostEnvironment.isCredential name ->
                        at
                            $"{name} looks like a credential: never write one in config; use \"host:<NAME>\" (the host's variable) or \"secret:<name>\" (an fx secret)"
                    | _ -> ()
        ]

    /// The variables of a map that `check` accepts, in name order, as requests from one origin.
    let private requestsOf (origin: VariableOrigin) (variables: Map<string, string>) : VariableRequest list =
        variables
        |> Map.toList
        |> List.filter (fun (name, value) -> check (Map.ofList [ name, value ]) |> List.isEmpty)
        |> List.map (fun (name, value) ->
            let source = parse value
            let credential = isCredential name source

            {
                Name = name
                Source = source
                Origin = origin
                Credential = credential
                NeedsConsent = credential && origin = VariableOrigin.ProjectSpec
            })

    let private named (a: string) (b: string) =
        String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

    /// What an environment is asked to get: the person's `env.variables`, then the spec's
    /// `variables` (from `specOrigin`'s file) for names the person did not set (names compared
    /// without case, as Windows does); without the names `deny` matches (glob patterns, without case)
    /// and without what `check` refuses. Also those `deny` removed, to record.
    let requests
        (person: Map<string, string>)
        (spec: Map<string, string>)
        (specOrigin: VariableOrigin)
        (deny: string list)
        : VariableRequest list * VariableRequest list =
        let mine = requestsOf VariableOrigin.Person person

        let theirs =
            requestsOf specOrigin spec
            |> List.filter (fun r -> not (mine |> List.exists (fun m -> named m.Name r.Name)))

        let denied (request: VariableRequest) =
            deny
            |> List.exists (fun pattern -> HostEnvironment.matches pattern request.Name)

        List.partition (denied >> not) (mine @ theirs)

    /// Why `env.deny` left a variable out.
    [<Literal>]
    let DeniedByHost = "env.deny matches it"

    /// The record of a variable.
    let record (request: VariableRequest) (outcome: VariableOutcome) : EnvVariable =
        {
            Name = request.Name
            Source = request.Source.Reference
            Origin = request.Origin
            Credential = request.Credential
            Outcome = outcome
        }

    /// A value for a request, checked: a credential's value must be long enough to redact.
    let accept (request: VariableRequest) (value: string) : Result<string, string> =
        if request.Credential && value.Length < MinSecretLength then
            Error
                $"its value is shorter than {MinSecretLength} characters: redacting it from output would mangle that output"
        else
            Ok value

    /// What the environment gets from the requests' outcomes: the values of those passed.
    let resolved (outcomes: (VariableRequest * Result<string, VariableOutcome>) list) : ResolvedVariables =
        let passed =
            outcomes
            |> List.choose (fun (request, outcome) ->
                match outcome with
                | Ok value -> Some(request, value)
                | Error _ -> None)

        {
            Values = passed |> List.map (fun (r, value) -> r.Name, value)
            Secrets =
                passed
                |> List.filter (fun (r, _) -> r.Credential)
                |> List.map (fun (r, value) -> r.Name, value)
            Record =
                outcomes
                |> List.map (fun (request, outcome) ->
                    match outcome with
                    | Ok _ -> record request VariableOutcome.Passed
                    | Error outcome -> record request outcome)
        }

    /// One line per variable for people and the environment's notes: where it came from and what
    /// became of it.
    let notes (record: EnvVariable list) : string list = record |> List.map string

    /// The base variables with these set (a later one replaces an earlier of the same name, without
    /// case).
    let extend (base': (string * string) list) (values: (string * string) list) : (string * string) list =
        let replaced =
            base'
            |> List.filter (fun (name, _) -> not (values |> List.exists (fun (n, _) -> named n name)))

        replaced @ values

    let private outcome: Codec<VariableOutcome> =
        Codec.union "kind" {
            let! passed = Codec.case0 "passed" VariableOutcome.Passed

            and! granted =
                Codec.case1 "granted" VariableOutcome.Granted (Field.required "grant" Codec.string)

            and! denied =
                Codec.case1 "denied" VariableOutcome.Denied (Field.required "reason" Codec.string)

            and! unavailable =
                Codec.case1 "unavailable" VariableOutcome.Unavailable (Field.required "reason" Codec.string)

            return
                fun value ->
                    match value with
                    | VariableOutcome.Passed -> passed
                    | VariableOutcome.Granted grant -> granted grant
                    | VariableOutcome.Denied reason -> denied reason
                    | VariableOutcome.Unavailable reason -> unavailable reason
        }
        |> Codec.named "VariableOutcome"

    let private origin: Codec<VariableOrigin> =
        Codec.enum
            [
                VariableOrigin.Person, "person"
                VariableOrigin.HostSpec, "host"
                VariableOrigin.ProjectSpec, "project"
            ]
        |> Codec.named "VariableOrigin"

    /// A variable as an environment's events record it: `{"name", "source", "origin", "credential",
    /// "outcome"}`, never its value.
    let codec: Codec<EnvVariable> =
        Codec.record {
            let! name =
                Codec.field "name" (fun (v: EnvVariable) -> v.Name) (Codec.string |> Codec.nonEmpty)

            and! source =
                Codec.field "source" (fun v -> v.Source) (Codec.string |> Codec.nonEmpty)

            and! origin' = Codec.field "origin" (fun v -> v.Origin) origin
            and! credential = Codec.field "credential" (fun v -> v.Credential) Codec.bool
            and! outcome' = Codec.field "outcome" (fun v -> v.Outcome) outcome

            return
                {
                    Name = name
                    Source = source
                    Origin = origin'
                    Credential = credential
                    Outcome = outcome'
                }
        }
        |> Codec.describe "A variable an environment was asked to get: its name, source and outcome, never its value."
        |> Codec.named "EnvVariable"

/// The person's choice of variables (privileged: host config or the command line, never a project).
/// Core slots outside `Fx.Variants`: the host registers `VariableSlots.all` with `Slots.all`.
[<RequireQualifiedAccess>]
module VariableSlots =
    /// `env.variables`: variables every environment's commands get, by name: `host:<NAME>`,
    /// `secret:<name>` or a literal (not for credential-like names). The person's own: no consent.
    let variables: Slot<Map<string, string>> =
        Slot.create
            "env.variables"
            "Variables every environment's commands get, by name: \"host:<NAME>\" (the host's own variable, e.g. GH_TOKEN), \"secret:<name>\" (an fx secret) or a literal value (not for names that look like credentials). Credentials are redacted from what the model sees."
            (Codec.stringMap Codec.string
             |> Codec.refine (fun variables ->
                 match EnvVariables.check variables with
                 | [] -> Ok()
                 | issues -> Error(issues |> List.map string |> String.concat "; ")))
            Map.empty
        |> Slot.privileged

    /// `env.deny`: names (glob patterns, without case) no environment gets, from any origin.
    let deny: Slot<string list> =
        Slot.create
            "env.deny"
            "Variable names (patterns with *, compared without case) no environment's commands get, whoever asks for them."
            (Codec.list (Codec.string |> Codec.nonEmpty))
            []
        |> Slot.privileged

    /// The slots, for the host's registry.
    let all: SlotInfo list = [ Slot.info variables; Slot.info deny ]

/// Exact-match redaction of credential values (spec §18): every occurrence of a value becomes
/// `[redacted NAME]`. Encoded copies (base64, URL or JSON escapes) are not found (T601).
[<NoEquality; NoComparison>]
type Redaction =
    {
        /// Name and UTF-8 bytes of each value.
        Values: (string * byte array) list
    }

    member this.IsEmpty = this.Values.IsEmpty

    override this.ToString() =
        $"redaction of {this.Values.Length} values"

[<RequireQualifiedAccess>]
module Redaction =
    let private utf8 = UTF8Encoding(false)

    /// Redacts nothing.
    let none: Redaction = { Values = [] }

    /// Redacts these values (name, value); empty values are ignored.
    let create (secrets: (string * string) list) : Redaction =
        {
            Values =
                secrets
                |> List.filter (fun (_, value) -> value <> "")
                |> List.map (fun (name, value) -> name, utf8.GetBytes value)
                |> List.distinctBy snd
        }

    /// What replaces a value.
    let marker (names: string list) : string =
        "[redacted " + String.concat "," names + "]"

    let private matchesAt (bytes: byte array) (start: int) (value: byte array) =
        start + value.Length <= bytes.Length
        && ReadOnlySpan<byte>(bytes, start, value.Length).SequenceEqual(ReadOnlySpan<byte>(value))

    /// Where values occur in the bytes: merged regions (start, end) with the names found in each,
    /// in order. A value inside another is the outer one's; values that overlap are one region.
    let private regions (redaction: Redaction) (bytes: byte array) : (int * int * string list) list =
        let found =
            [
                for name, value in redaction.Values do
                    let mutable start = 0

                    while start >= 0 && start <= bytes.Length - value.Length do
                        let at =
                            ReadOnlySpan<byte>(bytes, start, bytes.Length - start).IndexOf(ReadOnlySpan<byte>(value))

                        if at < 0 then
                            start <- -1
                        else
                            yield start + at, start + at + value.Length, name
                            start <- start + at + 1
            ]
            |> List.sortBy (fun (s, e, _) -> s, -e)

        found
        |> List.fold
            (fun (merged: (int * int * string list) list) (s, e, name) ->
                match merged with
                // A value inside the region found so far adds nothing; one that reaches past it joins it.
                | (_, me, _) :: _ when s < me && e <= me -> merged
                | (ms, me, names) :: rest when s < me ->
                    let names = if List.contains name names then names else names @ [ name ]
                    (ms, e, names) :: rest
                | _ -> (s, e, [ name ]) :: merged)
            []
        |> List.rev

    let private replace (bytes: byte array) (regions: (int * int * string list) list) : byte array =
        if regions.IsEmpty then
            bytes
        else
            use out = new IO.MemoryStream(bytes.Length)
            let mutable at = 0

            for s, e, names in regions do
                out.Write(bytes, at, s - at)
                let mark = utf8.GetBytes(marker names)
                out.Write(mark, 0, mark.Length)
                at <- e

            out.Write(bytes, at, bytes.Length - at)
            out.ToArray()

    /// The bytes with every value replaced.
    let bytes (redaction: Redaction) (input: byte array) : byte array =
        if redaction.IsEmpty then
            input
        else
            replace input (regions redaction input)

    /// The text with every value replaced.
    let text (redaction: Redaction) (input: string) : string =
        if
            redaction.IsEmpty
            || not (
                redaction.Values
                |> List.exists (fun (_, value) -> input.Contains(utf8.GetString value, StringComparison.Ordinal))
            )
        then
            input
        else
            utf8.GetString(bytes redaction (utf8.GetBytes input))

    /// The bytes with every value masked by something of the same length (the marker, cut or padded
    /// with `*`): for pieces of a file read by offset, whose lengths readers count on.
    let sameLength (redaction: Redaction) (input: byte array) : byte array =
        if redaction.IsEmpty then
            input
        else
            let output = Array.copy input

            for s, e, names in regions redaction input do
                let mark = utf8.GetBytes(marker names)

                for i in s .. e - 1 do
                    output[i] <- if i - s < mark.Length then mark[i - s] else byte '*'

            output

    /// The most bytes a value has: how far a value read in pieces can reach back.
    let longest (redaction: Redaction) : int =
        redaction.Values |> List.fold (fun m (_, v) -> max m v.Length) 0

    /// Redacts output read in pieces: `pending` (held back before) and the new piece give what can be
    /// passed on now, and what to hold back: the shortest end that could still begin a value (and
    /// never a value found in part). Nothing else is held back, so output that ends a line or a
    /// message is passed on at once.
    let step (redaction: Redaction) (pending: byte array) (piece: byte array) : byte array * byte array =
        let buffer = Array.append pending piece

        if redaction.IsEmpty then
            buffer, [||]
        else
            let n = buffer.Length

            // The earliest position from which the rest of the buffer is the beginning of a value.
            let partial =
                seq { max 0 (n - longest redaction + 1) .. n - 1 }
                |> Seq.tryFind (fun start ->
                    redaction.Values
                    |> List.exists (fun (_, value) ->
                        value.Length > n - start
                        && ReadOnlySpan<byte>(buffer, start, n - start)
                            .SequenceEqual(ReadOnlySpan<byte>(value, 0, n - start))))
                |> Option.defaultValue n

            let found = regions redaction buffer

            // A region that reaches past the cut is held back whole, so it is found whole later.
            let cut =
                found
                |> List.fold (fun cut (s, e, _) -> if s < cut && e > cut then s else cut) partial

            let ready = found |> List.filter (fun (_, e, _) -> e <= cut)
            let head = replace (Array.sub buffer 0 cut) ready
            head, Array.sub buffer cut (n - cut)

    /// What is held back at the end of the output, redacted.
    let flush (redaction: Redaction) (pending: byte array) : byte array = bytes redaction pending
