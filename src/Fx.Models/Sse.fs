namespace Fx.Models

open System
open System.Text
open Fx.Contracts

/// One server-sent event (the WHATWG event-stream format, which model providers stream).
type SseEvent =
    {
        /// The `event` field; `message` when the event names none.
        Event: string
        /// The `data` lines, joined with line feeds.
        Data: string
        /// The last event id seen so far, if any.
        Id: string option
        /// The `retry` field of this event, if any (milliseconds).
        Retry: int option
    }

    override this.ToString() =
        $"event {this.Event} ({this.Data.Length} chars of data)"

/// Why a stream could not be read.
type StreamError =
    /// One event (or line) is larger than the limit.
    | TooLarge of limit: int
    /// A line is not valid JSON (NDJSON), at this 1-based line number.
    | InvalidLine of line: int * error: string

    override this.ToString() =
        match this with
        | TooLarge limit -> $"an event is larger than {limit} characters"
        | InvalidLine(line, error) -> $"line {line} is not JSON: {error}"

/// An incremental parser of event streams: give it the body's bytes as they arrive, in pieces of any
/// size, and it returns the events completed so far. Pieces may split lines, line endings (`\r\n`)
/// and UTF-8 sequences anywhere; the events are the same however the bytes were split.
///
/// Follows the WHATWG parsing rules: lines end with `\r\n`, `\n` or `\r`; a leading byte order mark is
/// skipped; `:` lines are comments; a blank line dispatches the event (none if it has no data); one
/// space after the colon is dropped; `id` with a NUL is ignored; an unfinished event at the end is
/// dropped (`Finish` says whether there was one). Invalid UTF-8 becomes U+FFFD. `maxChars` bounds
/// the memory one event may take.
[<Sealed>]
type SseParser(maxChars: int) =
    let decoder = UTF8Encoding(false).GetDecoder()
    let line = StringBuilder()
    let data = StringBuilder()
    let mutable eventType = ""
    let mutable lastId: string option = None
    let mutable retry: int option = None
    let mutable afterCr = false
    let mutable atStart = true
    let mutable hasData = false
    let mutable failed: StreamError option = None

    let dispatch (events: ResizeArray<SseEvent>) =
        if hasData then
            let text = data.ToString()

            let text =
                if text.EndsWith '\n' then
                    text.Substring(0, text.Length - 1)
                else
                    text

            events.Add
                {
                    Event = (if eventType = "" then "message" else eventType)
                    Data = text
                    Id = lastId
                    Retry = retry
                }

        data.Clear() |> ignore
        hasData <- false
        eventType <- ""
        retry <- None

    let field (name: string) (value: string) =
        match name with
        | "event" -> eventType <- value
        | "data" ->
            data.Append(value).Append('\n') |> ignore
            hasData <- true
        | "id" ->
            if not (value.Contains '\000') then
                lastId <- (if value = "" then None else Some value)
        | "retry" ->
            if value <> "" && value |> Seq.forall Char.IsAsciiDigit then
                match Int32.TryParse value with
                | true, ms -> retry <- Some ms
                | _ -> ()
        | _ -> ()

    let endLine (events: ResizeArray<SseEvent>) =
        let text = line.ToString()
        line.Clear() |> ignore

        if text = "" then
            dispatch events
        elif text.StartsWith ':' then
            ()
        else
            match text.IndexOf ':' with
            | -1 -> field text ""
            | colon ->
                let value = text.Substring(colon + 1)

                let value = if value.StartsWith ' ' then value.Substring 1 else value

                field (text.Substring(0, colon)) value

    let push (chars: char array) (start: int) (count: int) (events: ResizeArray<SseEvent>) =
        let mutable i = start

        while i < count && failed.IsNone do
            let c = chars[i]

            if c = '\n' && afterCr then
                afterCr <- false
            elif c = '\r' then
                afterCr <- true
                endLine events
            elif c = '\n' then
                afterCr <- false
                endLine events
            else
                afterCr <- false
                line.Append c |> ignore

                if line.Length + data.Length > maxChars then
                    failed <- Some(TooLarge maxChars)

            if data.Length > maxChars then
                failed <- Some(TooLarge maxChars)

            i <- i + 1

    /// A parser that allows events of up to 64 Mi characters.
    new() = SseParser(64 * 1024 * 1024)

    /// Parses the next piece of the stream; returns the events it completed.
    member _.Push(bytes: byte array) : Result<SseEvent list, StreamError> =
        match failed with
        | Some e -> Error e
        | None ->
            let chars =
                Array.zeroCreate<char>(decoder.GetCharCount(bytes, 0, bytes.Length, false))

            let count = decoder.GetChars(bytes, 0, bytes.Length, chars, 0, false)
            let events = ResizeArray<SseEvent>()

            // Skip one byte order mark at the very start.
            let start =
                if atStart && count > 0 then
                    atStart <- false
                    if chars[0] = '\uFEFF' then 1 else 0
                else
                    0

            push chars start count events

            match failed with
            | Some e -> Error e
            | None -> Ok(List.ofSeq events)

    /// Ends the stream. Returns whether an unfinished event was dropped.
    member _.Finish() : bool =
        let chars = Array.zeroCreate<char>(decoder.GetCharCount([||], 0, 0, true))
        let count = decoder.GetChars([||], 0, 0, chars, 0, true)

        for i in 0 .. count - 1 do
            line.Append chars[i] |> ignore

        let unfinished = line.Length > 0 || hasData
        line.Clear() |> ignore
        data.Clear() |> ignore
        hasData <- false
        unfinished

/// An incremental parser of newline-delimited JSON (one value per line; blank lines skipped), with
/// the same guarantees about pieces as `SseParser`.
[<Sealed>]
type NdjsonParser(maxChars: int) =
    let decoder = UTF8Encoding(false).GetDecoder()
    let line = StringBuilder()
    let mutable lineNumber = 0
    let mutable failed: StreamError option = None

    let endLine (values: ResizeArray<Json>) =
        lineNumber <- lineNumber + 1
        let text = line.ToString().TrimEnd('\r')
        line.Clear() |> ignore

        if not (String.IsNullOrWhiteSpace text) then
            match Json.parse text with
            | Ok value -> values.Add value
            | Error e -> failed <- Some(InvalidLine(lineNumber, string e))

    new() = NdjsonParser(64 * 1024 * 1024)

    /// Parses the next piece; returns the values of the lines it completed.
    member _.Push(bytes: byte array) : Result<Json list, StreamError> =
        match failed with
        | Some e -> Error e
        | None ->
            let chars =
                Array.zeroCreate<char>(decoder.GetCharCount(bytes, 0, bytes.Length, false))

            let count = decoder.GetChars(bytes, 0, bytes.Length, chars, 0, false)
            let values = ResizeArray<Json>()
            let mutable i = 0

            while i < count && failed.IsNone do
                if chars[i] = '\n' then
                    endLine values
                else
                    line.Append chars[i] |> ignore

                    if line.Length > maxChars then
                        failed <- Some(TooLarge maxChars)

                i <- i + 1

            match failed with
            | Some e -> Error e
            | None -> Ok(List.ofSeq values)

    /// Ends the stream: the value of a last line without a line feed, if any.
    member _.Finish() : Result<Json list, StreamError> =
        match failed with
        | Some e -> Error e
        | None ->
            let chars = Array.zeroCreate<char>(decoder.GetCharCount([||], 0, 0, true))
            let count = decoder.GetChars([||], 0, 0, chars, 0, true)

            for i in 0 .. count - 1 do
                line.Append chars[i] |> ignore

            let values = ResizeArray<Json>()

            if line.Length > 0 then
                endLine values

            match failed with
            | Some e -> Error e
            | None -> Ok(List.ofSeq values)

[<RequireQualifiedAccess>]
module Sse =
    /// Parses a whole stream given in pieces (for tests and recorded bodies). Unfinished events at
    /// the end are dropped.
    let parseAll (pieces: byte array seq) : Result<SseEvent list, StreamError> =
        let parser = SseParser()

        let events =
            pieces
            |> Seq.fold
                (fun acc piece ->
                    match acc with
                    | Error e -> Error e
                    | Ok events -> parser.Push piece |> Result.map (fun more -> events @ more))
                (Ok [])

        parser.Finish() |> ignore
        events

    /// The text of an event in the stream format (`event:` and `data:` lines, then a blank line).
    let render (event: SseEvent) : string =
        let builder = StringBuilder()

        if event.Event <> "message" then
            builder.Append("event: ").Append(event.Event).Append('\n') |> ignore

        for line in event.Data.Split('\n') do
            builder.Append("data: ").Append(line).Append('\n') |> ignore

        match event.Id with
        | Some id -> builder.Append("id: ").Append(id).Append('\n') |> ignore
        | None -> ()

        builder.Append('\n').ToString()

    /// An event named `event` with this data.
    let event (name: string) (data: string) : SseEvent =
        {
            Event = name
            Data = data
            Id = None
            Retry = None
        }
