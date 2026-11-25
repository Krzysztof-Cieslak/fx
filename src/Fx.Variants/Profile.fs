namespace Fx.Variants

open System
open System.Security.Cryptography
open System.Text
open Fx.Contracts

/// The id of a profile: the SHA-256 of its canonical JSON (`Profile.bytes`), written
/// `sha256:<64 lowercase hex digits>`. Equal profiles have equal ids on every machine, and storing a
/// profile's bytes in the blob store gives a blob whose hash is the profile's id.
[<Struct>]
type ProfileId =
    private
    | ProfileId of hex: string

    /// The digest as 64 lowercase hex digits.
    member this.Hex =
        let (ProfileId hex) = this
        hex

    /// The first 12 hex digits, for display (`fx`, dashboards).
    member this.Short = this.Hex.Substring(0, 12)

    override this.ToString() = "sha256:" + this.Hex

module ProfileId =
    [<Literal>]
    let private Prefix = "sha256:"

    /// The ECMAScript pattern of an id (anchored), for schemas.
    [<Literal>]
    let Pattern = "^sha256:[0-9a-f]{64}$"

    /// The id of some bytes: their SHA-256.
    let ofBytes (bytes: byte array) : ProfileId =
        ProfileId(Convert.ToHexStringLower(SHA256.HashData bytes))

    let private isHex (c: char) =
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')

    /// Reads `sha256:<hex>`, lowercase only: an id has one spelling, so ids compare as text.
    let tryParse (text: string) : ProfileId option =
        if
            text.Length = Prefix.Length + 64
            && text.StartsWith(Prefix, StringComparison.Ordinal)
            && text |> Seq.skip Prefix.Length |> Seq.forall isHex
        then
            Some(ProfileId(text.Substring Prefix.Length))
        else
            None

    /// `"sha256:…"`.
    let codec: Codec<ProfileId> =
        Codec.string
        |> Codec.pattern Pattern
        |> Codec.tryMap
            (fun text ->
                match tryParse text with
                | Some id -> Ok id
                | None -> Error "expected sha256: and 64 lowercase hex digits")
            string
        |> Codec.describe "A profile id: the SHA-256 of the profile's canonical JSON."

/// The version of an extension a profile ran with, and the hash of its manifest when known.
type ExtensionVersion =
    {
        Version: string
        /// `sha256:` of the manifest (spec §16.1), which pins the exact package.
        Hash: string option
    }

    override this.ToString() =
        match this.Hash with
        | Some hash -> $"{this.Version} ({hash})"
        | None -> this.Version

/// How a subject (an interpreter, a provider, a component) did in one conformance suite.
type SuiteResult =
    {
        Passed: int
        Failed: int
        Skipped: int
    }

    override this.ToString() =
        $"{this.Passed} passed, {this.Failed} failed, {this.Skipped} skipped"

/// A profile (spec §15): everything that decides how a session, run or eval case behaves, hashed into
/// one id. Every record of such a stream refers to its profile by that id (docs/modules/variants.md),
/// and evals compare and publish results by profile.
///
/// - `Selections`: the value of every registered slot, defaults included, so a changed default changes
///   the id.
/// - `Extensions`, `Conformance`, `Lockfile`: filled by the host from the extension registry (T108),
///   the conformance suites the selected subjects passed, and the component lockfile (T110).
///
/// Maps make the id independent of the order things were added in; values are canonical JSON.
type Profile =
    {
        /// The version of this document's shape (1). A new shape gets a new number, so old ids stay
        /// valid for the documents they name.
        Format: int
        /// The version of the binary: the core and its bundled packages.
        Core: string
        Selections: Map<SlotId, Json>
        /// By extension id.
        Extensions: Map<string, ExtensionVersion>
        /// By subject, then by suite.
        Conformance: Map<string, Map<string, SuiteResult>>
        /// `sha256:` of the component lockfile, once there is one.
        Lockfile: string option
    }

    override this.ToString() =
        $"Profile(core {this.Core}, {this.Selections.Count} selections, {this.Extensions.Count} extensions)"

/// One way two profiles differ.
type ProfileDifference =
    | CoreChanged of before: string * after: string
    | SelectionChanged of slot: SlotId * before: Json option * after: Json option
    | ExtensionChanged of id: string * before: ExtensionVersion option * after: ExtensionVersion option
    | ConformanceChanged of subject: string * suite: string * before: SuiteResult option * after: SuiteResult option
    | LockfileChanged of before: string option * after: string option
    | FormatChanged of before: int * after: int

    override this.ToString() =
        let show (value: 'T option) (render: 'T -> string) =
            match value with
            | Some value -> render value
            | None -> "(none)"

        match this with
        | CoreChanged(before, after) -> $"core: {before} -> {after}"
        | SelectionChanged(slot, before, after) ->
            $"{slot}: {show before Json.serialize} -> {show after Json.serialize}"
        | ExtensionChanged(id, before, after) -> $"extension {id}: {show before string} -> {show after string}"
        | ConformanceChanged(subject, suite, before, after) ->
            $"conformance {subject} {suite}: {show before string} -> {show after string}"
        | LockfileChanged(before, after) -> $"lockfile: {show before string} -> {show after string}"
        | FormatChanged(before, after) -> $"format: {before} -> {after}"

module Profile =
    /// The format this code writes.
    [<Literal>]
    let CurrentFormat = 1

    /// A profile of a resolution: every slot's value, with no extensions, conformance results or
    /// lockfile yet.
    let ofResolution (core: string) (resolution: Resolution) : Profile =
        {
            Format = CurrentFormat
            Core = core
            Selections = resolution.Selections
            Extensions = Map.empty
            Conformance = Map.empty
            Lockfile = None
        }

    let withExtension (id: string) (version: ExtensionVersion) (profile: Profile) : Profile =
        { profile with
            Extensions = profile.Extensions.Add(id, version)
        }

    let withConformance (subject: string) (suite: string) (result: SuiteResult) (profile: Profile) : Profile =
        let suites =
            profile.Conformance.TryFind subject
            |> Option.defaultValue Map.empty
            |> Map.add suite result

        { profile with
            Conformance = profile.Conformance.Add(subject, suites)
        }

    let withLockfile (hash: string) (profile: Profile) : Profile = { profile with Lockfile = Some hash }

    let private sha256Text = Codec.string |> Codec.pattern "^sha256:[0-9a-f]{64}$"

    let private extensionVersion: Codec<ExtensionVersion> =
        Codec.record {
            let! version =
                Codec.field "version" (fun (e: ExtensionVersion) -> e.Version) Codec.string

            and! hash = Codec.optionalField "hash" (fun e -> e.Hash) sha256Text
            return { Version = version; Hash = hash }
        }

    let private count = Codec.int |> Codec.atLeast 0

    let private suiteResult: Codec<SuiteResult> =
        Codec.record {
            let! passed = Codec.field "passed" (fun (r: SuiteResult) -> r.Passed) count
            and! failed = Codec.field "failed" (fun r -> r.Failed) count
            and! skipped = Codec.field "skipped" (fun r -> r.Skipped) count

            return
                {
                    Passed = passed
                    Failed = failed
                    Skipped = skipped
                }
        }

    let private selections: Codec<Map<SlotId, Json>> =
        Codec.stringMap Codec.json
        |> Codec.tryMap
            (fun map ->
                let invalid =
                    map
                    |> Map.toList
                    |> List.choose (fun (key, _) ->
                        match SlotId.tryCreate key with
                        | Ok _ -> None
                        | Error reason -> Some $"\"{key}\": {reason}")

                match invalid with
                | [] ->
                    Ok(
                        map
                        |> Map.toSeq
                        |> Seq.map (fun (key, value) -> SlotId.create key, Json.canonicalize value)
                        |> Map.ofSeq
                    )
                | reasons -> Error(String.Join("; ", reasons)))
            (fun map -> map |> Map.toSeq |> Seq.map (fun (id, value) -> id.Value, value) |> Map.ofSeq)

    /// The JSON form of a profile. The id is the SHA-256 of its canonical serialization, so this codec
    /// is part of every stored id: change it only with a new `format`.
    let codec: Codec<Profile> =
        Codec.record {
            let! format =
                Codec.field "format" (fun (p: Profile) -> p.Format) (Codec.int |> Codec.between 1 CurrentFormat)

            and! core = Codec.field "core" (fun p -> p.Core) Codec.string
            and! selections = Codec.field "selections" (fun p -> p.Selections) selections

            and! extensions =
                Codec.field "extensions" (fun p -> p.Extensions) (Codec.stringMap extensionVersion)

            and! conformance =
                Codec.field "conformance" (fun p -> p.Conformance) (Codec.stringMap (Codec.stringMap suiteResult))

            and! lockfile = Codec.optionalField "lockfile" (fun p -> p.Lockfile) sha256Text

            return
                {
                    Format = format
                    Core = core
                    Selections = selections
                    Extensions = extensions
                    Conformance = conformance
                    Lockfile = lockfile
                }
        }
        |> Codec.describe
            "A profile: the value of every variant slot, the extension versions, the conformance results and the component lockfile's hash."
        |> Codec.named "Profile"

    /// The canonical JSON text of a profile (`Json.serializeCanonical`).
    let canonicalText (profile: Profile) : string =
        Json.serializeCanonical (codec.Encode profile)

    /// The UTF-8 bytes of the canonical JSON: what is hashed, and what the blob store keeps.
    let bytes (profile: Profile) : byte array =
        Encoding.UTF8.GetBytes(canonicalText profile)

    /// The profile's id.
    let id (profile: Profile) : ProfileId = ProfileId.ofBytes (bytes profile)

    /// Reads stored bytes back, checking that they are the profile with this id.
    let ofBytes (expected: ProfileId) (content: byte array) : Result<Profile, string> =
        let actual = ProfileId.ofBytes content

        if actual <> expected then
            Error $"the content is profile {actual}, not {expected}"
        else
            match Json.parse (Encoding.UTF8.GetString content) with
            | Error error -> Error $"invalid JSON: {error}"
            | Ok json ->
                match codec.Decode json with
                | Error issues -> Error(Issue.render issues)
                | Ok profile -> Ok profile

    /// The selections of a profile as an explicit layer: resolving it again with the current
    /// registry reproduces its values, gives new slots their defaults, and reports slots that no
    /// longer exist (counterfactual and partial replay, spec §15).
    let layer (profile: Profile) : Selections =
        Selections.create
            (Source.create Layer.Explicit $"profile {id profile}")
            (profile.Selections
             |> Map.toList
             |> List.map (fun (slot, value) -> slot.Value, value))

    let private diffMaps
        (before: Map<'K, 'V>)
        (after: Map<'K, 'V>)
        (difference: 'K -> 'V option -> 'V option -> ProfileDifference)
        : ProfileDifference list =
        Seq.append (Map.keys before) (Map.keys after)
        |> Seq.distinct
        |> Seq.sort
        |> Seq.choose (fun key ->
            let a = before.TryFind key
            let b = after.TryFind key

            if a = b then None else Some(difference key a b))
        |> List.ofSeq

    /// How two profiles differ, in a fixed order: format, core, selections by slot, extensions by id,
    /// conformance by subject and suite, lockfile.
    let diff (before: Profile) (after: Profile) : ProfileDifference list =
        let conformance =
            let flatten (profile: Profile) =
                profile.Conformance
                |> Map.toSeq
                |> Seq.collect (fun (subject, suites) ->
                    suites |> Map.toSeq |> Seq.map (fun (suite, result) -> (subject, suite), result))
                |> Map.ofSeq

            diffMaps (flatten before) (flatten after) (fun (subject, suite) a b ->
                ConformanceChanged(subject, suite, a, b))

        [
            if before.Format <> after.Format then
                FormatChanged(before.Format, after.Format)
            if before.Core <> after.Core then
                CoreChanged(before.Core, after.Core)
            yield! diffMaps before.Selections after.Selections (fun slot a b -> SelectionChanged(slot, a, b))
            yield! diffMaps before.Extensions after.Extensions (fun id a b -> ExtensionChanged(id, a, b))
            yield! conformance
            if before.Lockfile <> after.Lockfile then
                LockfileChanged(before.Lockfile, after.Lockfile)
        ]
