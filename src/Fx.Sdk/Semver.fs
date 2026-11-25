namespace Fx.Sdk

open System
open System.Globalization
open Fx.Contracts

/// A semantic version (semver.org 2.0): `major.minor.patch`, an optional pre-release
/// (`-beta.2`) and optional build metadata (`+a7733435c`). Components are versioned with it.
///
/// Equality is structural (build metadata included); order is semver precedence (`SemVer.compare`),
/// which ignores build metadata. So it has no structural comparison.
[<NoComparison>]
type SemVer =
    {
        Major: int
        Minor: int
        Patch: int
        /// Pre-release identifiers, e.g. `["beta"; "2"]`; empty for a release.
        Prerelease: string list
        /// Build metadata identifiers; ignored by precedence.
        Build: string list
    }

    override this.ToString() =
        let pre =
            if this.Prerelease.IsEmpty then
                ""
            else
                "-" + String.Join(".", this.Prerelease)

        let build =
            if this.Build.IsEmpty then
                ""
            else
                "+" + String.Join(".", this.Build)

        $"{this.Major}.{this.Minor}.{this.Patch}{pre}{build}"

[<RequireQualifiedAccess>]
module SemVer =

    let private isDigits (s: string) =
        s.Length > 0 && s |> Seq.forall Char.IsAsciiDigit

    /// A numeric identifier: digits without a leading zero, within `int`.
    let internal number (s: string) : int option =
        if isDigits s && (s.Length = 1 || s[0] <> '0') then
            match Int32.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture) with
            | true, n -> Some n
            | _ -> None
        else
            None

    let private isIdentifier (s: string) =
        s.Length > 0
        && s |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '-')

    let private identifiers (numericNoLeadingZero: bool) (text: string) : string list option =
        let parts = text.Split '.' |> List.ofArray

        let valid (part: string) =
            isIdentifier part
            && (not numericNoLeadingZero || not (isDigits part) || (number part).IsSome)

        if parts |> List.forall valid then Some parts else None

    /// Parses `major.minor.patch[-pre][+build]` strictly (no leading `v`, no leading zeros).
    let tryParse (text: string) : SemVer option =
        let core, build =
            match text.IndexOf '+' with
            | -1 -> text, Some []
            | i -> text.Substring(0, i), identifiers false (text.Substring(i + 1))

        let core, pre =
            match core.IndexOf '-' with
            | -1 -> core, Some []
            | i -> core.Substring(0, i), identifiers true (core.Substring(i + 1))

        match core.Split '.', pre, build with
        | [| major; minor; patch |], Some pre, Some build ->
            match number major, number minor, number patch with
            | Some major, Some minor, Some patch ->
                Some
                    {
                        Major = major
                        Minor = minor
                        Patch = patch
                        Prerelease = pre
                        Build = build
                    }
            | _ -> None
        | _ -> None

    let parse (text: string) : Result<SemVer, string> =
        match tryParse text with
        | Some version -> Ok version
        | None -> Error $"expected a semantic version such as \"1.2.3\", got {Json.quote text}"

    let create (major: int) (minor: int) (patch: int) : SemVer =
        {
            Major = major
            Minor = minor
            Patch = patch
            Prerelease = []
            Build = []
        }

    let private compareIdentifier (a: string) (b: string) =
        match number a, number b with
        | Some x, Some y -> compare x y
        // Numeric identifiers have lower precedence than alphanumeric ones.
        | Some _, None -> -1
        | None, Some _ -> 1
        | None, None -> String.CompareOrdinal(a, b) |> sign

    let rec private comparePre (a: string list) (b: string list) =
        match a, b with
        | [], [] -> 0
        | [], _ -> -1
        | _, [] -> 1
        | x :: xs, y :: ys ->
            match compareIdentifier x y with
            | 0 -> comparePre xs ys
            | c -> c

    /// Semver precedence: numbers, then a release above its pre-releases, then pre-release
    /// identifiers left to right. Build metadata is ignored. Returns -1, 0 or 1.
    let compare (a: SemVer) (b: SemVer) : int =
        match Operators.compare (a.Major, a.Minor, a.Patch) (b.Major, b.Minor, b.Patch) with
        | 0 ->
            match a.Prerelease, b.Prerelease with
            | [], [] -> 0
            | [], _ -> 1
            | _, [] -> -1
            | x, y -> comparePre x y
        | c -> sign c

    let isPrerelease (version: SemVer) = not version.Prerelease.IsEmpty

    let codec: Codec<SemVer> =
        Codec.string
        |> Codec.tryMap parse string
        |> Codec.describe "A semantic version, e.g. \"1.4.5\"."

/// A comparison against a version, the primitive of a range.
[<RequireQualifiedAccess>]
type Comparator =
    | Less of SemVer
    | LessOrEqual of SemVer
    | Greater of SemVer
    | GreaterOrEqual of SemVer
    | Equal of SemVer

    member this.Version =
        match this with
        | Less v
        | LessOrEqual v
        | Greater v
        | GreaterOrEqual v
        | Equal v -> v

    override this.ToString() =
        match this with
        | Less v -> "<" + string v
        | LessOrEqual v -> "<=" + string v
        | Greater v -> ">" + string v
        | GreaterOrEqual v -> ">=" + string v
        | Equal v -> "=" + string v

/// A set of versions, written as npm writes ranges: `1.4.5` or `=1.4.5` (exactly), `>=1.4 <2`
/// (every comparator), `^1.4`, `~1.4.5`, `1`, `1.x`, `*`, and alternatives joined by `||`.
/// Partial versions are X-ranges: `1` is `>=1.0.0 <2.0.0-0`. A pre-release version is in a range
/// only if a comparator of the matching alternative names a pre-release of the same
/// `major.minor.patch` (npm's rule), so `>=1.4` never picks `2.0.0-beta`.
[<CustomEquality; NoComparison>]
type VersionRange =
    {
        /// The text it was parsed from.
        Text: string
        /// Alternatives (`||`), each a list of comparators that must all hold.
        Sets: Comparator list list
    }

    override this.Equals(other: obj) =
        match other with
        | :? VersionRange as other -> this.Sets = other.Sets
        | _ -> false

    override this.GetHashCode() = hash this.Sets

    override this.ToString() = this.Text

[<RequireQualifiedAccess>]
module VersionRange =

    let private pre0 (major: int) (minor: int) (patch: int) : SemVer =
        { SemVer.create major minor patch with
            Prerelease = [ "0" ]
        }

    /// A partial version: missing or wildcard parts are `None`.
    type private Partial =
        {
            Major: int option
            Minor: int option
            Patch: int option
            Prerelease: string list
            Build: string list
        }

    let private isWildcard (s: string) = s = "*" || s = "x" || s = "X"

    let private parsePartial (text: string) : Partial option =
        if isWildcard text || text = "" then
            Some
                {
                    Major = None
                    Minor = None
                    Patch = None
                    Prerelease = []
                    Build = []
                }
        else
            match SemVer.tryParse text with
            | Some v ->
                Some
                    {
                        Major = Some v.Major
                        Minor = Some v.Minor
                        Patch = Some v.Patch
                        Prerelease = v.Prerelease
                        Build = v.Build
                    }
            | None ->
                let parts = text.Split '.'

                let part (i: int) =
                    if i >= parts.Length then Some None
                    elif isWildcard parts[i] then Some None
                    else SemVer.number parts[i] |> Option.map Some

                if parts.Length > 3 then
                    None
                else
                    match part 0, part 1, part 2 with
                    | Some major, Some minor, Some patch ->
                        // After a wildcard, every later part must be a wildcard too.
                        let ordered =
                            match major, minor, patch with
                            | None, Some _, _
                            | None, _, Some _
                            | _, None, Some _ -> false
                            | _ -> true

                        if ordered && major.IsSome then
                            Some
                                {
                                    Major = major
                                    Minor = minor
                                    Patch = patch
                                    Prerelease = []
                                    Build = []
                                }
                        elif ordered then
                            Some
                                {
                                    Major = None
                                    Minor = None
                                    Patch = None
                                    Prerelease = []
                                    Build = []
                                }
                        else
                            None
                    | _ -> None

    let private full (p: Partial) : SemVer =
        {
            Major = defaultArg p.Major 0
            Minor = defaultArg p.Minor 0
            Patch = defaultArg p.Patch 0
            Prerelease = p.Prerelease
            Build = p.Build
        }

    /// The primitive comparators of one written comparator.
    let private desugar (op: string) (p: Partial) : Comparator list =
        let any = [ Comparator.GreaterOrEqual(SemVer.create 0 0 0) ]

        match op, p.Major, p.Minor, p.Patch with
        | ("" | "="), None, _, _ -> any
        | ("" | "="), Some major, None, _ ->
            [
                Comparator.GreaterOrEqual(SemVer.create major 0 0)
                Comparator.Less(pre0 (major + 1) 0 0)
            ]
        | ("" | "="), Some major, Some minor, None ->
            [
                Comparator.GreaterOrEqual(SemVer.create major minor 0)
                Comparator.Less(pre0 major (minor + 1) 0)
            ]
        | ("" | "="), Some _, Some _, Some _ -> [ Comparator.Equal(full p) ]
        | "^", None, _, _ -> any
        | "^", Some major, _, _ when major > 0 ->
            [ Comparator.GreaterOrEqual(full p); Comparator.Less(pre0 (major + 1) 0 0) ]
        | "^", Some 0, None, _ -> [ Comparator.Less(pre0 1 0 0) ]
        | "^", Some 0, Some minor, _ when minor > 0 || p.Patch.IsNone ->
            [ Comparator.GreaterOrEqual(full p); Comparator.Less(pre0 0 (minor + 1) 0) ]
        | "^", Some 0, Some 0, Some patch ->
            [ Comparator.GreaterOrEqual(full p); Comparator.Less(pre0 0 0 (patch + 1)) ]
        | "~", None, _, _ -> any
        | "~", Some major, None, _ -> [ Comparator.GreaterOrEqual(full p); Comparator.Less(pre0 (major + 1) 0 0) ]
        | "~", Some major, Some minor, _ ->
            [ Comparator.GreaterOrEqual(full p); Comparator.Less(pre0 major (minor + 1) 0) ]
        | ">", None, _, _ -> [ Comparator.Less(pre0 0 0 0) ]
        | ">", Some major, None, _ -> [ Comparator.GreaterOrEqual(SemVer.create (major + 1) 0 0) ]
        | ">", Some major, Some minor, None -> [ Comparator.GreaterOrEqual(SemVer.create major (minor + 1) 0) ]
        | ">", Some _, Some _, Some _ -> [ Comparator.Greater(full p) ]
        | ">=", _, _, _ -> [ Comparator.GreaterOrEqual(full p) ]
        | "<", None, _, _ -> [ Comparator.Less(pre0 0 0 0) ]
        | "<", Some _, Some _, Some _ -> [ Comparator.Less(full p) ]
        | "<", _, _, _ -> [ Comparator.Less({ full p with Prerelease = [ "0" ] }) ]
        | "<=", None, _, _ -> any
        | "<=", Some major, None, _ -> [ Comparator.Less(pre0 (major + 1) 0 0) ]
        | "<=", Some major, Some minor, None -> [ Comparator.Less(pre0 major (minor + 1) 0) ]
        | "<=", _, _, _ -> [ Comparator.LessOrEqual(full p) ]
        | _ -> any

    let private operators = [ ">="; "<="; ">"; "<"; "="; "^"; "~" ]

    let private parseComparator (token: string) : Result<Comparator list, string> =
        let op =
            operators
            |> List.tryFind (fun op -> token.StartsWith(op, StringComparison.Ordinal))
            |> Option.defaultValue ""

        let rest = token.Substring op.Length

        if rest = "" && op <> "" then
            Error $"{Json.quote op} needs a version after it"
        else
            match parsePartial rest with
            | Some partial -> Ok(desugar op partial)
            | None -> Error $"{Json.quote token} is not a version or a comparison with one"

    /// Joins an operator separated from its version by spaces (`>= 1.4`) back to it.
    let private tokens (set: string) : string list =
        let words =
            set.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
            |> List.ofArray

        let rec join (words: string list) =
            match words with
            | op :: version :: rest when List.contains op operators -> (op + version) :: join rest
            | word :: rest -> word :: join rest
            | [] -> []

        join words

    let private parseSet (set: string) : Result<Comparator list, string> =
        let tokens = tokens set

        if tokens.IsEmpty then
            Ok [ Comparator.GreaterOrEqual(SemVer.create 0 0 0) ]
        else
            tokens
            |> List.fold
                (fun acc token ->
                    match acc, parseComparator token with
                    | Ok comparators, Ok more -> Ok(comparators @ more)
                    | Error e, _
                    | _, Error e -> Error e)
                (Ok [])

    let parse (text: string) : Result<VersionRange, string> =
        if text.Trim() = "" then
            Error "expected a version range such as \">=1.4 <2\", got an empty string"
        else
            let sets = text.Split("||") |> List.ofArray |> List.map parseSet

            match
                sets
                |> List.tryPick (function
                    | Error e -> Some e
                    | Ok _ -> None)
            with
            | Some error -> Error $"invalid version range {Json.quote text}: {error}"
            | None ->
                Ok
                    {
                        Text = text
                        Sets =
                            sets
                            |> List.choose (function
                                | Ok set -> Some set
                                | Error _ -> None)
                    }

    let private test (version: SemVer) (comparator: Comparator) =
        let c = SemVer.compare version comparator.Version

        match comparator with
        | Comparator.Less _ -> c < 0
        | Comparator.LessOrEqual _ -> c <= 0
        | Comparator.Greater _ -> c > 0
        | Comparator.GreaterOrEqual _ -> c >= 0
        | Comparator.Equal _ -> c = 0

    let private sameCore (a: SemVer) (b: SemVer) =
        a.Major = b.Major && a.Minor = b.Minor && a.Patch = b.Patch

    let private satisfiesSet (version: SemVer) (set: Comparator list) =
        set |> List.forall (test version)
        && (not (SemVer.isPrerelease version)
            || set
               |> List.exists (fun c -> SemVer.isPrerelease c.Version && sameCore c.Version version))

    /// Whether the version is in the range.
    let satisfies (range: VersionRange) (version: SemVer) : bool =
        range.Sets |> List.exists (satisfiesSet version)

    /// Exactly one version.
    let exactly (version: SemVer) : VersionRange =
        {
            Text = string version
            Sets = [ [ Comparator.Equal version ] ]
        }

    /// Every release.
    let any: VersionRange =
        {
            Text = "*"
            Sets = [ [ Comparator.GreaterOrEqual(SemVer.create 0 0 0) ] ]
        }

    let codec: Codec<VersionRange> =
        Codec.string
        |> Codec.tryMap parse string
        |> Codec.describe
            "A version range, as npm writes them: \"1.4.5\", \">=1.4 <2\", \"^1.4\", \"~1.4.5\", \"1.x\", \"*\", alternatives with \"||\"."
