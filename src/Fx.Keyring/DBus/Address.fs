namespace Fx.Keyring.DBus

open System
open System.Text

/// Where a bus listens, as far as this client connects: Unix domain sockets only.
[<RequireQualifiedAccess>]
type BusEndpoint =
    /// A socket file, e.g. `/run/user/1000/bus`.
    | Path of string
    /// A Linux abstract socket name (without the leading NUL).
    | Abstract of string

    override this.ToString() =
        match this with
        | Path path -> "unix:path=" + path
        | Abstract name -> "unix:abstract=" + name

/// D-Bus server addresses (https://dbus.freedesktop.org/doc/dbus-specification.html#addresses).
module BusAddress =
    let private unescape (value: string) : Result<string, string> =
        let bytes = ResizeArray<byte>()
        let mutable error = None
        let mutable i = 0

        while error.IsNone && i < value.Length do
            match value[i] with
            | '%' when i + 2 < value.Length ->
                match Byte.TryParse(value.AsSpan(i + 1, 2), Globalization.NumberStyles.HexNumber, null) with
                | true, b ->
                    bytes.Add b
                    i <- i + 3
                | _ -> error <- Some "invalid %-escape in a bus address"
            | '%' -> error <- Some "truncated %-escape in a bus address"
            | c when c > '\u007f' -> error <- Some "non-ASCII character in a bus address"
            | c ->
                bytes.Add(byte c)
                i <- i + 1

        match error with
        | Some e -> Error e
        | None -> Ok(Encoding.UTF8.GetString(bytes.ToArray()))

    let private parseOne (entry: string) : Result<BusEndpoint option, string> =
        match entry.IndexOf ':' with
        | -1 -> Error "a bus address needs a transport (e.g. unix:path=...)"
        | colon ->
            let transport = entry.Substring(0, colon)

            let pairs =
                entry.Substring(colon + 1).Split(',', StringSplitOptions.RemoveEmptyEntries)
                |> Array.toList
                |> List.map (fun pair ->
                    match pair.IndexOf '=' with
                    | -1 -> Error "a bus address option needs a value"
                    | eq ->
                        unescape (pair.Substring(eq + 1))
                        |> Result.map (fun v -> pair.Substring(0, eq), v))

            match
                pairs
                |> List.tryPick (fun p ->
                    match p with
                    | Error e -> Some e
                    | Ok _ -> None)
            with
            | Some e -> Error e
            | None ->
                let options =
                    pairs
                    |> List.choose (fun p ->
                        match p with
                        | Ok kv -> Some kv
                        | Error _ -> None)

                let find key =
                    options |> List.tryFind (fun (k, _) -> k = key) |> Option.map snd

                if transport <> "unix" then
                    Ok None
                else
                    match find "path", find "abstract" with
                    | Some path, None -> Ok(Some(BusEndpoint.Path path))
                    | None, Some name -> Ok(Some(BusEndpoint.Abstract name))
                    | Some _, Some _ -> Error "a unix bus address has both path and abstract"
                    // dir, tmpdir and runtime are for servers only.
                    | None, None -> Ok None

    /// The endpoints of an address, in order. Transports other than Unix sockets (tcp, launchd,
    /// autolaunch, …) are skipped; an address with none left is an error.
    let parse (address: string) : Result<BusEndpoint list, string> =
        let entries =
            address.Split(';', StringSplitOptions.RemoveEmptyEntries) |> Array.toList

        let parsed = entries |> List.map parseOne

        match
            parsed
            |> List.tryPick (fun p ->
                match p with
                | Error e -> Some e
                | Ok _ -> None)
        with
        | Some e -> Error e
        | None ->
            match
                parsed
                |> List.choose (fun p ->
                    match p with
                    | Ok(Some e) -> Some e
                    | _ -> None)
            with
            | [] -> Error "no Unix socket in the bus address"
            | endpoints -> Ok endpoints
