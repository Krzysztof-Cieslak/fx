/// The TypeScript types of the session protocol (spec §17: generated types for clients), from the
/// codecs. `bun scripts/declarations.ts` type-checks the golden file.
module Fx.Sessions.Host.Tests.DeclarationTests

open Expecto
open Fx.Contracts.TypeScript
open Fx.Store
open Fx.Sessions
open Fx.Sessions.Host

let private file: DeclarationFile =
    {
        Layout = Layout.Module
        Types =
            [
                Codecs.command.Schema
                Codecs.event.Schema
                Codecs.state.Schema
                SessionLog.feed.Schema
                RequestKinds.confirm.Payload.Schema
                RequestKinds.choice.Payload.Schema
            ]
        Members = []
    }

[<Tests>]
let tests =
    testList
        "Session declarations"
        [
            test "the session protocol's types are generated from the codecs" {
                Golden.expect "session.d.ts" (DeclarationFile.render file)
            }

            test "the envelope types are the event store's" {
                // Both declare these names; a declaration file needs one body per name.
                Expect.equal Codecs.attribution.Schema Fx.Store.Codecs.attribution.Schema "Attribution"
                Expect.equal Codecs.eventRef.Schema Fx.Store.Codecs.eventRef.Schema "EventRef"
                Expect.equal Codecs.streamId.Schema Fx.Store.Codecs.streamId.Schema "StreamId"
            }
        ]
