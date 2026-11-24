/// Server-sent events framing (`text/event-stream`, WHATWG HTML §9.2). Pure; `LocalServer.Events`
/// writes the frames and flushes after each one.
module Fx.Transport.Http.Sse

open System
open System.Text

/// One event. Clients reconnect with the last `Id` they saw in a `Last-Event-ID` header.
type Event =
    {
        Id: string option
        /// The `event:` field; clients treat a missing type as `message`.
        Type: string option
        /// Any text; line breaks become several `data:` lines and come back as `\n`.
        Data: string
    }

    override this.ToString() =
        let show = Option.defaultValue "-"
        $"event id {show this.Id}, type {show this.Type}, {this.Data.Length} chars"

let contentType = "text/event-stream"

/// An event with data only.
let event (data: string) : Event = { Id = None; Type = None; Data = data }

let private requireSingleLine (field: string) (value: string) =
    if value.IndexOfAny [| '\r'; '\n'; '\000' |] >= 0 then
        invalidArg field $"an event {field} must not contain line breaks or NUL"

/// The frame for an event: `id:`, `event:` and one `data:` line per line of data, then a blank line.
let format (e: Event) : string =
    let sb = StringBuilder()

    e.Id
    |> Option.iter (fun id ->
        requireSingleLine "id" id
        sb.Append("id: ").Append(id).Append('\n') |> ignore)

    e.Type
    |> Option.iter (fun t ->
        requireSingleLine "type" t
        sb.Append("event: ").Append(t).Append('\n') |> ignore)

    // Only CR, LF and CRLF end lines in an event stream; ReplaceLineEndings would also split U+2028.
    for line in e.Data.Replace("\r\n", "\n").Replace('\r', '\n').Split '\n' do
        sb.Append("data: ").Append(line).Append('\n') |> ignore

    sb.Append('\n').ToString()

/// A comment frame. Clients ignore it; it keeps idle streams alive through proxies and timeouts.
let comment (text: string) : string =
    requireSingleLine "comment" text
    $": {text}\n\n"
