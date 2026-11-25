namespace Fx.Environments

open System
open Fx.Sdk

/// The host's variables as an environment on the host's machine starts with them (spec §18:
/// credentials stay on the host; runtime injection vectors are never passed on). envd there, and
/// every process it starts, sees the host's variables without these.
[<RequireQualifiedAccess>]
module HostEnvironment =
    /// Names of variables that usually hold credentials (`*` matches any text; compared without
    /// case): API keys, tokens, secrets, passwords, private keys, credential files. The host reads
    /// its own credentials from these; an agent's commands must not.
    let credentialPatterns: string list =
        [
            "*_API_KEY*"
            "*_APIKEY*"
            "*_ACCESS_KEY*"
            "*SECRET*"
            "*PASSWORD*"
            "*PASSWD*"
            "TOKEN"
            "*_TOKEN"
            "*_TOKEN_*"
            "*_PRIVATE_KEY*"
            "*_CREDENTIALS*"
        ]

    /// Whether a name matches a pattern in which `*` stands for any text, without case.
    let matches (pattern: string) (name: string) : bool =
        let parts = pattern.Split '*'

        if parts.Length = 1 then
            String.Equals(pattern, name, StringComparison.OrdinalIgnoreCase)
        else
            let first = parts[0]
            let last = parts[parts.Length - 1]

            if
                name.Length < first.Length + last.Length
                || not (name.StartsWith(first, StringComparison.OrdinalIgnoreCase))
                || not (name.EndsWith(last, StringComparison.OrdinalIgnoreCase))
            then
                false
            else
                // The middle parts in order, between the prefix and the suffix.
                let mutable position = first.Length
                let limit = name.Length - last.Length
                let mutable ok = true

                for i in 1 .. parts.Length - 2 do
                    if ok then
                        let part = parts[i]

                        if part.Length > 0 then
                            let found =
                                name.IndexOf(part, position, limit - position, StringComparison.OrdinalIgnoreCase)

                            if found < 0 then
                                ok <- false
                            else
                                position <- found + part.Length

                ok

    /// Whether a variable looks like a credential (`credentialPatterns`).
    let isCredential (name: string) : bool =
        credentialPatterns |> List.exists (fun pattern -> matches pattern name)

    /// Whether a variable stays on the host: a credential or a runtime injection vector.
    let isDenied (name: string) : bool =
        isCredential name || InjectionVectors.isVector name

    /// The host's variables without the denied ones, in their order.
    let scrub (vars: (string * string) list) : (string * string) list =
        vars |> List.filter (fun (name, _) -> not (isDenied name))
