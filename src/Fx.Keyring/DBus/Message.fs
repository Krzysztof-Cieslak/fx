namespace Fx.Keyring.DBus

open System

/// A D-Bus value (https://dbus.freedesktop.org/doc/dbus-specification.html#type-system). Arrays
/// carry their element signature, so an empty array has a type.
[<RequireQualifiedAccess>]
type DBusValue =
    | Byte of byte
    | Boolean of bool
    | Int16 of int16
    | UInt16 of uint16
    | Int32 of int32
    | UInt32 of uint32
    | Int64 of int64
    | UInt64 of uint64
    | Double of double
    | String of string
    | ObjectPath of string
    | Signature of string
    | UnixFd of uint32
    | Variant of DBusValue
    /// `ay`, kept as bytes.
    | Bytes of byte array
    | Array of elementSignature: string * items: DBusValue list
    | Struct of DBusValue list
    | DictEntry of key: DBusValue * value: DBusValue

    /// The value's type, as a signature.
    member this.Type: string =
        match this with
        | Byte _ -> "y"
        | Boolean _ -> "b"
        | Int16 _ -> "n"
        | UInt16 _ -> "q"
        | Int32 _ -> "i"
        | UInt32 _ -> "u"
        | Int64 _ -> "x"
        | UInt64 _ -> "t"
        | Double _ -> "d"
        | String _ -> "s"
        | ObjectPath _ -> "o"
        | Signature _ -> "g"
        | UnixFd _ -> "h"
        | Variant _ -> "v"
        | Bytes _ -> "ay"
        | Array(element, _) -> "a" + element
        | Struct items -> "(" + String.concat "" (items |> List.map (fun i -> i.Type)) + ")"
        | DictEntry(key, value) -> "{" + key.Type + value.Type + "}"

    override this.ToString() =
        match this with
        | Byte v -> $"byte {v}"
        | Boolean v -> if v then "true" else "false"
        | Int16 v -> $"int16 {v}"
        | UInt16 v -> $"uint16 {v}"
        | Int32 v -> $"int32 {v}"
        | UInt32 v -> $"uint32 {v}"
        | Int64 v -> $"int64 {v}"
        | UInt64 v -> $"uint64 {v}"
        | Double v -> $"double {v}"
        | String v -> "\"" + v + "\""
        | ObjectPath v -> "path " + v
        | Signature v -> "signature " + v
        | UnixFd v -> $"fd {v}"
        | Variant v -> $"<{v}>"
        | Bytes v -> $"bytes[{v.Length}]"
        | Array(_, items) -> "[" + String.concat ", " (items |> List.map string) + "]"
        | Struct items -> "(" + String.concat ", " (items |> List.map string) + ")"
        | DictEntry(k, v) -> $"{k}: {v}"

module DBusValue =
    /// `a{sv}`: a dictionary of variants keyed by strings.
    let vardict (entries: (string * DBusValue) list) : DBusValue =
        DBusValue.Array(
            "{sv}",
            entries
            |> List.map (fun (k, v) -> DBusValue.DictEntry(DBusValue.String k, DBusValue.Variant v))
        )

    /// `a{ss}`: a dictionary of strings.
    let stringDict (entries: (string * string) list) : DBusValue =
        DBusValue.Array(
            "{ss}",
            entries
            |> List.map (fun (k, v) -> DBusValue.DictEntry(DBusValue.String k, DBusValue.String v))
        )

    /// `ao`: object paths.
    let paths (items: string list) : DBusValue =
        DBusValue.Array("o", items |> List.map DBusValue.ObjectPath)

/// The four kinds of D-Bus message.
[<RequireQualifiedAccess>]
type MessageType =
    | MethodCall
    | MethodReturn
    | Error
    | Signal

    member this.Code: byte =
        match this with
        | MethodCall -> 1uy
        | MethodReturn -> 2uy
        | Error -> 3uy
        | Signal -> 4uy

    override this.ToString() =
        match this with
        | MethodCall -> "method_call"
        | MethodReturn -> "method_return"
        | Error -> "error"
        | Signal -> "signal"

/// A D-Bus message: the header fields this client uses, and the body. The body's signature is
/// derived from its values.
type Message =
    {
        Type: MessageType
        /// `NO_REPLY_EXPECTED` (1), `NO_AUTO_START` (2), `ALLOW_INTERACTIVE_AUTHORIZATION` (4).
        Flags: byte
        Serial: uint32
        Path: string option
        Interface: string option
        Member: string option
        ErrorName: string option
        ReplySerial: uint32 option
        Destination: string option
        Sender: string option
        Body: DBusValue list
    }

    member this.Signature = String.concat "" (this.Body |> List.map (fun v -> v.Type))

    override this.ToString() =
        let part (name: string) (value: string option) =
            value |> Option.map (fun v -> $" {name}={v}") |> Option.defaultValue ""

        $"{this.Type} #{this.Serial}"
        + part "path" this.Path
        + part "interface" this.Interface
        + part "member" this.Member
        + part "error" this.ErrorName
        + part "reply-to" (this.ReplySerial |> Option.map string)
        + part "destination" this.Destination
        + part "sender" this.Sender
        + $" ({this.Signature})"

module Message =
    [<Literal>]
    let NoReplyExpected = 1uy

    [<Literal>]
    let NoAutoStart = 2uy

    let private empty =
        {
            Type = MessageType.MethodCall
            Flags = 0uy
            Serial = 0u
            Path = None
            Interface = None
            Member = None
            ErrorName = None
            ReplySerial = None
            Destination = None
            Sender = None
            Body = []
        }

    /// A method call; the connection assigns the serial.
    let call (destination: string) (path: string) (iface: string) (methodName: string) (body: DBusValue list) =
        { empty with
            Path = Some path
            Interface = Some iface
            Member = Some methodName
            Destination = Some destination
            Body = body
        }

    /// A reply to a call, as a service would send it (used by tests).
    let reply (call: Message) (body: DBusValue list) =
        { empty with
            Type = MessageType.MethodReturn
            ReplySerial = Some call.Serial
            Destination = call.Sender
            Body = body
        }

    /// An error reply to a call, as a service would send it (used by tests).
    let error (call: Message) (name: string) (text: string) =
        { empty with
            Type = MessageType.Error
            ErrorName = Some name
            ReplySerial = Some call.Serial
            Destination = call.Sender
            Body = [ DBusValue.String text ]
        }

    /// A signal, as a service would send it (used by tests).
    let signal (path: string) (iface: string) (name: string) (body: DBusValue list) =
        { empty with
            Type = MessageType.Signal
            Path = Some path
            Interface = Some iface
            Member = Some name
            Body = body
        }
