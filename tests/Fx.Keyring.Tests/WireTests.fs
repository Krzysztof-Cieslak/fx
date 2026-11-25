module Fx.Keyring.Tests.WireTests

open System
open Expecto
open Fx.Keyring.DBus

let private hex (bytes: byte array) = Convert.ToHexStringLower bytes

let private ascii (text: string) = hex (Text.Encoding.ASCII.GetBytes text)

let private ok (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error e -> failtest e

/// The Hello call every client sends first, worked out by hand from the specification: four header
/// fields, each a struct aligned to 8, and no body.
let private helloBytes =
    String.concat
        ""
        [
            "6c010001" + "00000000" + "01000000" + "6d000000" // 'l', call, no flags, v1; no body; serial 1; 109 bytes of fields
            "01016f00" + "15000000" + ascii "/org/freedesktop/DBus" + "00" + "0000" // PATH (o)
            "02017300" + "14000000" + ascii "org.freedesktop.DBus" + "00" + "000000" // INTERFACE (s)
            "03017300" + "05000000" + ascii "Hello" + "00" + "0000" // MEMBER (s)
            "06017300" + "14000000" + ascii "org.freedesktop.DBus" + "00" + "000000" // DESTINATION (s), padded to 128
        ]

let private hello =
    { Message.call "org.freedesktop.DBus" "/org/freedesktop/DBus" "org.freedesktop.DBus" "Hello" [] with
        Serial = 1u
    }

let private everyType =
    [
        DBusValue.Byte 0xffuy
        DBusValue.Boolean true
        DBusValue.Int16 -2s
        DBusValue.UInt16 65535us
        DBusValue.Int32 -3
        DBusValue.UInt32 4000000000u
        DBusValue.Int64 -5L
        DBusValue.UInt64 18000000000000000000UL
        DBusValue.Double 2.5
        DBusValue.String "zoë 🔑"
        DBusValue.ObjectPath "/org/freedesktop/secrets/collection/login/1"
        DBusValue.Signature "a{sv}(oayays)"
        DBusValue.UnixFd 3u
        DBusValue.Variant(DBusValue.Variant(DBusValue.UInt64 9UL))
        DBusValue.Bytes [| 0uy; 1uy; 2uy |]
        DBusValue.Bytes [||]
        DBusValue.Array("t", [ DBusValue.UInt64 1UL; DBusValue.UInt64 2UL ])
        DBusValue.Array("(ss)", [])
        DBusValue.vardict [ "Label", DBusValue.String "x"; "Locked", DBusValue.Boolean false ]
        DBusValue.stringDict [ "service", "fx"; "account", "a" ]
        DBusValue.Struct
            [
                DBusValue.ObjectPath "/s/1"
                DBusValue.Bytes [| 1uy |]
                DBusValue.Bytes [| 2uy; 3uy |]
                DBusValue.String "text/plain"
            ]
        DBusValue.paths [ "/"; "/a/b_c" ]
    ]

[<Tests>]
let tests =
    testList
        "D-Bus wire"
        [
            test "Hello encodes as the specification lays it out" {
                Expect.equal (hex (Wire.encode hello)) helloBytes "bytes"
                Expect.equal (Wire.frameLength (ReadOnlySpan(Convert.FromHexString helloBytes))) (Ok 128) "frame length"
            }

            test "bodies align every value to its type, relative to the body" {
                // OpenSession("plain", <"">): the variant's string is aligned to 4.
                Expect.equal
                    (hex (Wire.marshal [ DBusValue.String "plain"; DBusValue.Variant(DBusValue.String "") ]))
                    ("05000000" + ascii "plain" + "00" + "017300" + "000000" + "00000000" + "00")
                    "OpenSession"

                // a{sv} {"a": <uint32 7>}: entries aligned to 8, so 4 bytes of padding after the length,
                // which counts from the first entry.
                Expect.equal
                    (hex (Wire.marshal [ DBusValue.vardict [ "a", DBusValue.UInt32 7u ] ]))
                    ("10000000" + "00000000" + "01000000" + "6100" + "017500" + "000000" + "07000000")
                    "a{sv}"
            }

            test "every type round-trips through a message" {
                let message =
                    { Message.call "org.example" "/org/example" "org.example.I" "M" everyType with
                        Serial = 42u
                        Flags = Message.NoAutoStart
                    }

                let decoded = Wire.decode (Wire.encode message) |> ok
                Expect.equal decoded message "message"
                Expect.equal decoded.Signature "ybnqiuxtdsoghvayayata(ss)a{sv}a{ss}(oayays)ao" "signature"
            }

            test "replies, errors and signals round-trip" {
                let call = { hello with Sender = Some ":1.7" }

                for message in
                    [
                        { Message.reply call [ DBusValue.String ":1.7" ] with
                            Serial = 2u
                        }
                        { Message.error call "org.freedesktop.DBus.Error.ServiceUnknown" "no" with
                            Serial = 3u
                        }
                        { Message.signal
                              "/org/freedesktop/secrets/prompt/p1"
                              "org.freedesktop.Secret.Prompt"
                              "Completed"
                              [ DBusValue.Boolean false; DBusValue.Variant(DBusValue.paths [ "/c" ]) ] with
                            Serial = 4u
                        }
                    ] do
                    Expect.equal (Wire.decode (Wire.encode message) |> ok) message (string message)
            }

            test "big-endian messages decode" {
                let bytes =
                    Convert.FromHexString(
                        "42020001"
                        + "00000004"
                        + "00000009"
                        + "0000000f"
                        + "05017500"
                        + "00000007" // REPLY_SERIAL 7
                        + "08016700"
                        + "017500"
                        + "00" // SIGNATURE "u", padded
                        + "01020304"
                    )

                Expect.equal (Wire.frameLength (ReadOnlySpan bytes)) (Ok 36) "frame length"
                let message = Wire.decode bytes |> ok
                Expect.equal message.Type MessageType.MethodReturn "type"
                Expect.equal message.Serial 9u "serial"
                Expect.equal message.ReplySerial (Some 7u) "reply serial"
                Expect.equal message.Body [ DBusValue.UInt32 0x01020304u ] "body"
            }

            test "malformed messages are errors, not exceptions" {
                let good =
                    Wire.encode
                        { hello with
                            Body = [ DBusValue.Boolean true ]
                        }

                let corrupt (f: byte array -> unit) =
                    let copy = Array.copy good
                    f copy
                    Wire.decode copy

                Expect.isError (Wire.decode good[..20]) "truncated"
                Expect.isError (Wire.decode [||]) "empty"
                Expect.isError (corrupt (fun b -> b[0] <- byte 'x')) "byte order"
                Expect.isError (corrupt (fun b -> b[3] <- 2uy)) "version"
                Expect.isError (corrupt (fun b -> b[1] <- 9uy)) "type"
                Expect.isError (corrupt (fun b -> b[b.Length - 4] <- 2uy)) "boolean 2"
                Expect.isError (corrupt (fun b -> b[22] <- 1uy)) "non-zero padding"
                Expect.isError (Wire.decode (Array.append good [| 0uy |])) "trailing bytes"

                Expect.isError
                    (Wire.frameLength (ReadOnlySpan(Convert.FromHexString("6c010001ffffffff0100000000000000"))))
                    "too large"
            }

            test "invalid values are refused when sending" {
                let send body =
                    Wire.encode { hello with Body = body } |> ignore

                Expect.throwsT<ArgumentException> (fun () -> send [ DBusValue.ObjectPath "relative" ]) "path"
                Expect.throwsT<ArgumentException> (fun () -> send [ DBusValue.ObjectPath "/trailing/" ]) "trailing /"
                Expect.throwsT<ArgumentException> (fun () -> send [ DBusValue.String "a\u0000b" ]) "NUL"

                Expect.throwsT<ArgumentException>
                    (fun () -> send [ DBusValue.Array("s", [ DBusValue.UInt32 1u ]) ])
                    "element type"

                Expect.throwsT<ArgumentException> (fun () -> send [ DBusValue.Struct [] ]) "empty struct"
            }

            test "signatures parse into complete types" {
                Expect.equal (Wire.parseSignature "a{sv}(oayays)" |> List.map string) [ "a{sv}"; "(oayays)" ] "types"

                for invalid in [ "a"; "(i"; "()"; "a{vs}"; "a{s}"; "z"; String.replicate 256 "i" ] do
                    Expect.throwsT<IO.InvalidDataException> (fun () -> Wire.parseSignature invalid |> ignore) invalid
            }

            test "bus addresses: Unix paths and abstract names, escaped, other transports skipped" {
                Expect.equal
                    (BusAddress.parse "unix:path=/run/user/1000/bus")
                    (Ok [ BusEndpoint.Path "/run/user/1000/bus" ])
                    "path"

                Expect.equal
                    (BusAddress.parse "unix:abstract=/tmp/dbus-XYZ,guid=0123abcd")
                    (Ok [ BusEndpoint.Abstract "/tmp/dbus-XYZ" ])
                    "abstract"

                Expect.equal
                    (BusAddress.parse "tcp:host=localhost,port=1;unix:path=/a%20b%2c")
                    (Ok [ BusEndpoint.Path "/a b," ])
                    "escapes, tcp skipped"

                for invalid in
                    [
                        "tcp:host=localhost,port=1"
                        "unix:path=/a,abstract=/b"
                        "unix:path=%zz"
                        "unix:path=%2"
                        "unix:tmpdir=/tmp"
                        "garbage"
                        ""
                    ] do
                    Expect.isError (BusAddress.parse invalid) invalid
            }
        ]
