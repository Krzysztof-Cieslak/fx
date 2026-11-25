/// The TypeScript types of the grants log and its client protocol (spec §17: generated types for
/// clients), from the codecs. `bun scripts/declarations.ts` type-checks the golden file.
module Fx.Grants.Tests.DeclarationTests

open Expecto
open Fx.Contracts.TypeScript
open Fx.Grants

let private file: DeclarationFile =
    {
        Layout = Layout.Module
        Types =
            [
                Codecs.event.Schema
                Codecs.state.Schema
                Codecs.command.Schema
                Codecs.intent.Schema
                Codecs.ask.Schema
                Codecs.query.Schema
                Codecs.check.Schema
                Codecs.askOutcome.Schema
                Codecs.actionDef.Schema
                Codecs.policyRule.Schema
                Codecs.error.Schema
                ProtocolCodecs.listParams.Schema
                ProtocolCodecs.grantList.Schema
                ProtocolCodecs.pendingList.Schema
                ProtocolCodecs.answerParams.Schema
                ProtocolCodecs.draft.Schema
                ProtocolCodecs.revokeParams.Schema
                ProtocolCodecs.subscribeParams.Schema
                ProtocolCodecs.notice.Schema
            ]
        Members = []
    }

[<Tests>]
let tests =
    testList
        "Grant declarations"
        [
            test "the grants log's and protocol's types are generated from the codecs" {
                Golden.expect "grants.d.ts" (DeclarationFile.render file)
            }
        ]
