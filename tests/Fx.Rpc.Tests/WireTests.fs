module Fx.Rpc.Tests.WireTests

open System
open Expecto
open FsCheck
open FsCheck.FSharp
open Fx
open Fx.Contracts
open Fx.Rpc

let private parse (text: string) : string =
    match Wire.parse text with
    | Incoming.Single(Parsed.Valid m) -> "valid " + Wire.serialize m
    | Incoming.Single(Parsed.InvalidRequest(Some id, e)) -> $"invalid request {id}: {e.Message} ({e.Code})"
    | Incoming.Single(Parsed.InvalidRequest(None, e)) -> $"invalid request: {e.Message} ({e.Code})"
    | Incoming.Single(Parsed.InvalidResponse reason) -> "invalid response: " + reason
    | Incoming.Batch entries -> $"batch of {entries.Length}"
    | Incoming.Unreadable e -> $"unreadable: {e.Message} ({e.Code})"

let private id: Gen<RequestId> =
    Gen.oneof
        [
            Gen.choose64 (Int64.MinValue, Int64.MaxValue) |> Gen.map RequestId.Number
            ArbMap.defaults
            |> ArbMap.generate<string>
            |> Gen.map (fun s -> RequestId.String(if String.IsNullOrEmpty s then "" else s))
        ]

let private value: Gen<Json> =
    Gen.oneof
        [
            Gen.constant Json.Null
            Gen.elements [ true; false ] |> Gen.map Json.Bool
            Gen.choose (-1000, 1000) |> Gen.map Json.int
            Gen.elements [ ""; "a"; "é\n\"\\"; "\u2028🌍" ] |> Gen.map Json.String
        ]

let private parameters: Gen<Json option> =
    Gen.oneof
        [
            Gen.constant None
            Gen.listOf value |> Gen.map (fun items -> Some(Json.Array items))
            Gen.listOf value
            |> Gen.map (fun items -> Some(Json.Object(items |> List.mapi (fun i v -> $"k{i}", v))))
        ]

let private name =
    Gen.elements [ "a"; "session/send"; "_x.dev/y"; "$/cancelRequest"; "é" ]

let private message: Gen<Message> =
    Gen.oneof
        [
            Gen.map3 (fun i n p -> Message.Request(i, n, p)) id name parameters
            Gen.map2 (fun n p -> Message.Notification(n, p)) name parameters
            Gen.map2 (fun i v -> Message.Response(i, Ok v)) id value
            Gen.map3
                (fun i code d -> Message.Response(i, Error { Code = code; Message = "m"; Data = d }))
                id
                (Gen.choose (-33000, 1000))
                (Gen.optionOf value)
            Gen.choose (-33000, 0)
            |> Gen.map (fun code -> Message.ErrorWithoutId(RpcError.create code "e"))
        ]

[<Tests>]
let tests =
    testList
        "Wire"
        [
            testProperty "messages round-trip through text"
            <| Prop.forAll (Arb.fromGen message) (fun m ->
                match Wire.parse (Wire.serialize m) with
                | Incoming.Single(Parsed.Valid back) -> back = m
                | _ -> false)

            testProperty "batches round-trip in order"
            <| Prop.forAll (Arb.fromGen (Gen.nonEmptyListOf message)) (fun messages ->
                match Wire.parse (Wire.serializeBatch messages) with
                | Incoming.Batch entries -> entries = (messages |> List.map Parsed.Valid)
                | _ -> false)

            test "messages are written with members in a fixed order" {
                Expect.equal
                    (Wire.serialize (Message.Request(RequestId.Number 1L, "m", Some(Json.Object [ "a", Json.int 1 ]))))
                    """{"jsonrpc":"2.0","id":1,"method":"m","params":{"a":1}}"""
                    "request"

                Expect.equal
                    (Wire.serialize (Message.Notification("n", None)))
                    """{"jsonrpc":"2.0","method":"n"}"""
                    "notification"

                Expect.equal
                    (Wire.serialize (Message.Response(RequestId.String "s", Error(RpcError.methodNotFound "x"))))
                    """{"jsonrpc":"2.0","id":"s","error":{"code":-32601,"message":"method not found: \"x\""}}"""
                    "error"

                Expect.equal
                    (Wire.serialize (Message.ErrorWithoutId(RpcError.parseError "p")))
                    """{"jsonrpc":"2.0","id":null,"error":{"code":-32700,"message":"parse error: p"}}"""
                    "null id"
            }

            test "ids are strings or integers" {
                Expect.equal
                    (parse """{"jsonrpc":"2.0","id":9223372036854775807,"method":"m"}""")
                    """valid {"jsonrpc":"2.0","id":9223372036854775807,"method":"m"}"""
                    "int64"

                Expect.equal
                    (parse """{"jsonrpc":"2.0","id":1e2,"method":"m"}""")
                    """valid {"jsonrpc":"2.0","id":100,"method":"m"}"""
                    "integral"

                Expect.equal
                    (parse """{"jsonrpc":"2.0","id":1.5,"method":"m"}""")
                    "invalid request: invalid request: $.id: expected a string or an integer, got number 1.5 (-32600)"
                    "fraction"

                Expect.equal
                    (parse """{"jsonrpc":"2.0","id":null,"method":"m"}""")
                    "invalid request: invalid request: $.id: null ids are not supported; use a string or an integer (-32600)"
                    "null"

                Expect.equal
                    (parse """{"jsonrpc":"2.0","id":[1],"method":"m"}""")
                    "invalid request: invalid request: $.id: expected a string or an integer, got array of 1 item (-32600)"
                    "array"
            }

            test "invalid requests say what is wrong, and keep the id" {
                Expect.equal
                    (parse """{"jsonrpc":"2.0","id":3,"method":"m","params":"x"}""")
                    "invalid request 3: invalid request: $.params: expected an object or an array, got string \"x\" (-32600)"
                    "params"

                Expect.equal
                    (parse """{"jsonrpc":"2.0","id":"a","method":null}""")
                    "invalid request \"a\": invalid request: $.method: expected a string, got null (-32600)"
                    "method"

                Expect.equal
                    (parse """{"id":4,"method":"m"}""")
                    "invalid request 4: invalid request: $.jsonrpc: expected \"2.0\", got nothing (-32600)"
                    "version"

                Expect.equal
                    (parse """{"jsonrpc":"2.0","id":5}""")
                    "invalid request 5: invalid request: expected \"method\" (a request) or \"result\"/\"error\" (a response) (-32600)"
                    "neither"

                Expect.equal
                    (parse "7")
                    "invalid request: invalid request: expected a request object, got number 7 (-32600)"
                    "scalar"
            }

            test "malformed responses are only dropped" {
                Expect.equal
                    (parse """{"jsonrpc":"2.0","id":1,"result":1,"error":{"code":1,"message":"m"}}""")
                    "invalid response: a response has either \"result\" or \"error\", not both (id 1)"
                    "both"

                Expect.equal
                    (parse """{"jsonrpc":"2.0","id":1,"error":{"code":"x","message":"m"}}""")
                    "invalid response: $.error.code: expected an integer (id 1)"
                    "code"

                Expect.equal
                    (parse """{"jsonrpc":"2.0","result":1}""")
                    "invalid response: a response needs an id"
                    "no id"

                Expect.equal
                    (parse """{"jsonrpc":"1.0","id":1,"result":1}""")
                    "invalid response: $.jsonrpc: expected \"2.0\", got string \"1.0\""
                    "version"
            }

            test "text that is not JSON, and empty batches, are unreadable" {
                Expect.stringStarts (parse "{") "unreadable: parse error: " "parse"
                Expect.stringStarts (parse """{"a":1,"a":2}""") "unreadable: parse error: duplicate key" "duplicates"
                Expect.equal (parse "[]") "unreadable: invalid request: an empty batch (-32600)" "empty batch"
                Expect.equal (parse "[1, 2]") "batch of 2" "invalid entries"
            }

            testList
                "_meta"
                [
                    test "is split off params" {
                        let json =
                            Json.Object [ "a", Json.int 1; "_meta", Json.Object [ "x/y", Json.Bool true ] ]

                        Expect.equal
                            (Meta.split (Some json))
                            (Ok(Some(Json.Object [ "a", Json.int 1 ]), Map.ofList [ "x/y", Json.Bool true ]))
                            "split"

                        Expect.equal (Meta.split None) (Ok(None, Meta.empty)) "absent"
                        Expect.equal (Meta.split (Some(Json.Array []))) (Ok(Some(Json.Array []), Meta.empty)) "array"

                        Expect.equal
                            (Meta.split (Some(Json.Object [ "_meta", Json.int 1 ])) |> Result.mapError string)
                            (Error "$._meta: expected an object")
                            "not an object"
                    }

                    test "is attached to objects only" {
                        let meta = Map.ofList [ Protocol.metaKey "k", Json.int 1 ]
                        let key = Product.protocolNamespace + "/k"

                        Expect.equal
                            (Meta.attach meta None)
                            (Some(Json.Object [ "_meta", Json.Object [ key, Json.int 1 ] ]))
                            "absent"

                        Expect.equal
                            (Meta.attach meta (Some(Json.Object [ "a", Json.int 1 ])))
                            (Some(Json.Object [ "a", Json.int 1; "_meta", Json.Object [ key, Json.int 1 ] ]))
                            "object"

                        Expect.equal (Meta.attach Meta.empty (Some(Json.int 1))) (Some(Json.int 1)) "nothing to attach"

                        Expect.throwsT<ArgumentException>
                            (fun () -> Meta.attach meta (Some(Json.int 1)) |> ignore)
                            "number"
                    }
                ]

            testList
                "method names"
                [
                    test "follow the rules" {
                        for good in
                            [
                                "session/send"
                                "x"
                                "_acme.dev/buffers/list"
                                Protocol.ownExtensionMethod "debug/ping"
                            ] do
                            Expect.isNone (Protocol.checkMethodName good) good

                        for bad in [ ""; "rpc.discover"; "$/cancelRequest"; "_"; "_/x"; "_ns"; "_ns/"; "a\nb" ] do
                            Expect.isSome (Protocol.checkMethodName bad) bad

                        Expect.throwsT<ArgumentException>
                            (fun () -> Method.define "rpc.x" Codec.unit Codec.unit |> ignore)
                            "reserved"
                    }

                    test "extension methods and meta keys use the product's namespace" {
                        Expect.equal
                            (Protocol.ownExtensionMethod "a/b")
                            ("_" + Product.protocolNamespace + "/a/b")
                            "method"

                        Expect.isTrue (Protocol.isExtension (Protocol.ownExtensionMethod "a")) "extension"
                        Expect.equal (Protocol.metaKey "trace") (Product.protocolNamespace + "/trace") "meta key"

                        Expect.equal
                            Protocol.webSocketSubprotocol
                            (Product.protocolNamespace + ".jsonrpc")
                            "subprotocol"
                    }
                ]

            test "invalid params carry the issues as data" {
                let issues =
                    [
                        Issue.at [ PathSegment.Key "a" ] "bad"
                        Issue.at [ PathSegment.Index 2 ] "worse"
                    ]

                let error = RpcError.invalidParams issues
                Expect.equal error.Message "invalid params:\n$.a: bad\n$[2]: worse" "message"
                Expect.equal (RpcError.issues error) issues "data"
                Expect.equal (RpcError.issues (RpcError.create 1 "x")) [] "none"
            }

            test "internal errors never show the exception's message" {
                let error = RpcError.internalError (InvalidOperationException "password=hunter2")
                Expect.equal error.Message "internal error (InvalidOperationException)" "type only"
            }
        ]
