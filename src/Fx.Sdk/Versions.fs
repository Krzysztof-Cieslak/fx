namespace Fx.Sdk

open System
open System.Globalization

/// The versions an extension is built against (spec §16.1): the SDK's API version and the extension
/// protocol's version. A manifest names both; the host admits it only if it supports them.
[<RequireQualifiedAccess>]
module Versions =

    /// The version of the extension API this SDK implements: `major.minor`. Before 1.0 every minor
    /// version may break extensions; from 1.0 on, minor versions only add.
    [<Literal>]
    let Sdk = "0.1"

    /// The version of the extension protocol (host ↔ extension process, over JSON-RPC) this SDK
    /// speaks.
    [<Literal>]
    let Protocol = "1"

    /// A decimal number without sign or leading zeros.
    let private number (text: string) : int option =
        match Int32.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, n when string n = text -> Some n
        | _ -> None

    let private parse (version: string) : (int * int) option =
        match version.Split '.' with
        | [| major; minor |] -> Option.map2 (fun a b -> a, b) (number major) (number minor)
        | _ -> None

    /// Whether a version is `major.minor` (decimal, no leading zeros).
    let isSdkVersion (version: string) : bool = (parse version).IsSome

    /// Whether a host implementing SDK `host` can run an extension built against SDK `required`:
    /// the same major version and a minor version the host has (before 1.0, the same minor version).
    let sdkCompatible (host: string) (required: string) : bool =
        match parse host, parse required with
        | Some(hostMajor, hostMinor), Some(major, minor) ->
            hostMajor = major
            && (if major = 0 then minor = hostMinor else minor <= hostMinor)
        | _ -> false
