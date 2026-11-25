/// The extension protocol's messages: their wire format, which servers in every language write
/// (`ExtensionServer` here, `@fx/sdk` in TypeScript).
module Fx.Sdk.Process.Tests.ProtocolTests

open Expecto
open Fx.Contracts
open Fx.Sdk.Process

[<Tests>]
let tests =
    testList
        "Extension protocol"
        [
            test "typed errors: one kind per failure, as other hosts must write them" {
                let cases =
                    [
                        ExtensionProtocol.ProtocolError.UnknownExtension "x",
                        """{"kind":"unknownExtension","extension":"x"}""",
                        "unknown extension x"
                        ExtensionProtocol.ProtocolError.NotActive "x",
                        """{"kind":"notActive","extension":"x"}""",
                        "x is not active"
                        ExtensionProtocol.ProtocolError.ActivationFailed("x", "no runtime"),
                        """{"kind":"activationFailed","extension":"x","reason":"no runtime"}""",
                        "no runtime"
                        ExtensionProtocol.ProtocolError.UnknownContribution "x/t/i",
                        """{"kind":"unknownContribution","address":"x/t/i"}""",
                        "unknown contribution x/t/i"
                        ExtensionProtocol.ProtocolError.InProcessOnly "x/t/i",
                        """{"kind":"inProcessOnly","address":"x/t/i"}""",
                        "x/t/i runs only in its process"
                        ExtensionProtocol.ProtocolError.UnknownMethod "m",
                        """{"kind":"unknownMethod","method":"m"}""",
                        "unknown method m"
                        ExtensionProtocol.ProtocolError.Invalid [ Issue.at [ PathSegment.Key "a" ] "bad" ],
                        """{"kind":"invalid","issues":[{"path":["a"],"message":"bad"}]}""",
                        "invalid:\n$.a: bad"
                        ExtensionProtocol.ProtocolError.MethodFailed("too big", Json.int 3),
                        """{"kind":"methodFailed","message":"too big","data":3}""",
                        "too big"
                    ]

                for error, json, text in cases do
                    let codec = ExtensionProtocol.Codecs.protocolError
                    Expect.equal (Codec.encodeText codec error) json "the wire format"
                    Expect.equal (Codec.decodeText codec json) (Ok error) "it reads back"
                    Expect.equal (string error) text "what the host reports"
            }
        ]
