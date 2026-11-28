namespace Fx.CodeAct

open System
open Fx.Contracts
open Fx.Effects
open Fx.Kernel
open Fx.Models
open Fx.Interpreters
open Fx.Prompt

/// What a session's cells asked to pin and unpin (`fx.context.*`, spec §6.6: deliberate prompt
/// inclusion), for its turn to record as context events after the cell's receipt (T201c). One turn at a
/// time runs in a session, and its cells ask through its kernel's bridge.
[<Sealed>]
type ContextAsks() =
    let gate = obj ()
    let pending = ResizeArray<ContextEvent>()

    /// Asks to pin (`Pinned`) or unpin (`Unpinned`), in order.
    member _.Add(event: ContextEvent) = lock gate (fun () -> pending.Add event)

    /// What was asked since the last time, in order, which is then forgotten.
    member _.Take() : ContextEvent list =
        lock gate (fun () ->
            let asked = List.ofSeq pending
            pending.Clear()
            asked)

    /// What was asked and not recorded yet.
    member _.Pending: ContextEvent list = lock gate (fun () -> List.ofSeq pending)

    override _.ToString() = "context asks"

/// A pin as `context.pins` lists it.
type PinInfo =
    {
        Name: string
        /// `value` or `file`.
        Kind: string
        /// The file a file pin shows, relative to the workspace.
        Path: string option
        /// Its lines, `[first, last]`; none for the whole file.
        Lines: (int * int) option
        /// Characters it adds to every call, as it is now (`None`: the file cannot be read).
        Chars: int option
        /// Tokens that is, estimated.
        Tokens: int option
    }

    override this.ToString() = $"pin {this.Name} ({this.Kind})"

/// The `context` bindings of the host bridge (spec §6.5: `fx.context`, brokered by the host; §6.6).
[<RequireQualifiedAccess>]
module ContextBindings =
    let groups: (string * string) list =
        [
            "context", "What stays in view at every step: pinned values and live views of files, and what they cost."
        ]

    let private failed (code: string) (message: string) =
        HostCallError.Failed(code, message, None)

    /// The most characters a pinned value may have: it is sent with every call.
    let private maxValueChars (outputChars: int) = outputChars

    let private pinName: Codec<string> =
        Codec.string
        |> Codec.minLength 1
        |> Codec.maxLength 64
        |> Codec.pattern "^[A-Za-z0-9_.-]+$"
        |> Codec.describe "A name: 1 to 64 of A-Z a-z 0-9 _ . -; pinning a name again replaces its pin."

    /// The pins in effect: the transcript's, then the asks not recorded yet, in order.
    let current (transcript: Transcript) (pending: ContextEvent list) : PinSpec list =
        let mutable pins = transcript.Pins |> List.map _.Spec

        for event in pending do
            match event with
            | ContextEvent.Pinned spec -> pins <- (pins |> List.filter (fun p -> p.Name <> spec.Name)) @ [ spec ]
            | ContextEvent.Unpinned name -> pins <- pins |> List.filter (fun p -> p.Name <> name)
            | _ -> ()

        pins

    /// A value as pinned pieces: a string as its text, other data as JSON, a reference by reference.
    let valuePieces (name: string) (value: Value) : Piece list =
        match value with
        | Value.Inline(Json.String text) -> [ Piece.Text text ]
        | Value.Inline json -> [ Piece.Text(Json.serialize json) ]
        | Value.Blob(blob, preview) ->
            [
                Piece.Ref
                    {
                        Kind = "blob"
                        Id = string blob.Hash
                        Type = None
                        Preview = preview
                    }
            ]
        | Value.Handle(_, typeRef, preview) ->
            [
                Piece.Ref
                    {
                        Kind = "handle"
                        Id = name
                        Type = Some typeRef.Name
                        Preview = preview
                    }
            ]

    let private piecesChars (pieces: Piece list) =
        pieces |> List.sumBy (fun p -> (Piece.render p).Length)

    /// `context.pin(name, value)`: keeps a value in view at every step, as it is now.
    let pin<'K> (asks: ContextAsks) (outputChars: int) : HostBinding<'K> =
        HostBinding.create2
            "context.pin"
            "Keeps a value in view at every later step (it costs its size in every call), as it is now; pinning a name again replaces it. Unpin it with context.unpin when you no longer need it."
            (Arg.required "name" "" pinName,
             Arg.value "value" "A string shows as its text, other data as JSON, a handle by reference.")
            Codec.string
            (fun (name, value) ->
                let pieces = valuePieces name value
                let chars = piecesChars pieces

                if chars > maxValueChars outputChars then
                    EffResult.error (
                        failed
                            "tooLarge"
                            $"{chars} characters, more than {maxValueChars outputChars} can be pinned: pin a smaller part, or a file's lines with context.pinFile"
                    )
                else
                    asks.Add(ContextEvent.Pinned { Name = name; Pin = Pin.Value pieces })
                    EffResult.ok $"Pinned {name} ({chars} characters): it shows at every later step.")

    /// `context.pinFile(name, path, options?)`: keeps a live view of a workspace file (or some of its
    /// lines) in view at every step: it shows as the file is at each call.
    let pinFile<'K> (asks: ContextAsks) (settings: CoreSettings) : HostBinding<'K> =
        let options: Codec<(int * int) option> =
            Codec.record {
                let! lines =
                    Codec.optionalField
                        "lines"
                        id
                        (Codec.tuple2 (Codec.int |> Codec.atLeast 1) (Codec.int |> Codec.atLeast 1)
                         |> Codec.describe
                             "The first and the last line, 1-based and inclusive; the whole file by default.")

                return lines
            }
            |> Codec.named "PinFileOptions"

        HostBinding.create3
            "context.pinFile"
            "Keeps a live view of a workspace file, or some of its lines, in view at every later step: it shows as the file is at each call. Unpin it with context.unpin."
            (Arg.required "name" "" pinName,
             Arg.required "path" "Relative to the workspace." Codec.string,
             Arg.optional "options" "" options)
            Codec.string
            (fun (name, path, options) ->
                let relative =
                    CoreBindings.exclusionPath settings (CoreBindings.resolve settings path)

                if
                    relative = ""
                    || relative = ".."
                    || relative.StartsWith "../"
                    || WorkspacePath.isAbsolute relative
                then
                    EffResult.error (
                        failed "outside" $"{path} is not a file in the workspace: only those can be pinned"
                    )
                else
                    match Exclusions.test settings.Exclusions relative with
                    | Some found -> EffResult.error (CoreBindings.excluded relative found)
                    | None ->
                        let lines =
                            match options |> Option.flatten with
                            | Some(first, last) when last < first -> None
                            | Some(first, last) -> Some [ LineRange.create first last ]
                            | None -> Some []

                        match lines with
                        | None -> EffResult.error (failed "invalidLines" "the last line is before the first")
                        | Some lines ->
                            asks.Add(
                                ContextEvent.Pinned
                                    {
                                        Name = name
                                        Pin = Pin.File(relative, lines)
                                    }
                            )

                            EffResult.ok $"Pinned {name}: {relative} shows as it is at every later step.")

    /// `context.unpin(name)`.
    let unpin<'K> (asks: ContextAsks) (transcript: Eff<'K, Transcript>) : HostBinding<'K> =
        HostBinding.create1
            "context.unpin"
            "Stops showing a pin."
            (Arg.required "name" "" pinName)
            Codec.string
            (fun name ->
                eff {
                    let! t = transcript

                    if current t asks.Pending |> List.exists (fun p -> p.Name = name) then
                        asks.Add(ContextEvent.Unpinned name)
                        return Ok $"Unpinned {name}."
                    else
                        return Error(failed "notFound" $"nothing is pinned as {Json.quote name}")
                })

    let private pinInfo: Codec<PinInfo> =
        Codec.record {
            let! name = Codec.field "name" (fun (p: PinInfo) -> p.Name) Codec.string

            and! kind =
                Codec.field "kind" (fun p -> p.Kind) (Codec.string |> Codec.describe "value or file.")

            and! path = Codec.optionalField "path" (fun p -> p.Path) Codec.string

            and! lines =
                Codec.optionalField "lines" (fun p -> p.Lines) (Codec.tuple2 Codec.int Codec.int)

            and! chars =
                Codec.optionalField
                    "chars"
                    (fun p -> p.Chars)
                    (Codec.int
                     |> Codec.describe "Characters it adds to every call; absent when the file cannot be read.")

            and! tokens =
                Codec.optionalField "tokens" (fun p -> p.Tokens) (Codec.int |> Codec.describe "Estimated.")

            return
                {
                    Name = name
                    Kind = kind
                    Path = path
                    Lines = lines
                    Chars = chars
                    Tokens = tokens
                }
        }
        |> Codec.describe "A pin: what it shows and what it costs at every call."
        |> Codec.named "PinInfo"

    /// What a file pin shows now, read as `fs.read` reads it (content exclusions, the sandbox's policy
    /// through the hands): its lines, numbered, as a file view; `Error` says why it cannot show (not
    /// readable, or more than `maxChars`: it is sent with every call).
    let readPinned<'K when 'K :> Fx.Envd.IFsOps and 'K :> IScope>
        (settings: CoreSettings)
        (maxChars: int)
        (path: string)
        (lines: LineRange list)
        : Eff<'K, Result<FileView, string>> =
        let lines = LineRange.normalize lines

        let args =
            [
                Value.Inline(Json.String path)
                match lines with
                | [ range ] ->
                    Value.Inline(Json.Object [ "lines", Json.Array [ Json.int range.First; Json.int range.Last ] ])
                | _ -> ()
            ]

        eff {
            match! (CoreBindings.fsRead settings).Invoke args with
            | Ok(Value.Inline json) ->
                match CoreBindings.Codecs.fileText.Decode json with
                | Ok file ->
                    let text = Receipts.numbered file.Start (Receipts.splitLines file.Text)

                    if text.Length > maxChars then
                        return
                            Error
                                $"{text.Length} characters, more than {maxChars} can show at every call: pin some of its lines"
                    else
                        return
                            Ok
                                {
                                    Path = Paths.normalize path
                                    Digest = string file.Digest
                                    Lines = lines
                                    Text = text
                                }
                | Error _ -> return Error "it could not be read"
            | Ok _ -> return Error "it could not be read"
            | Error e -> return Error(string e)
        }

    /// `context.pins()`: what is pinned and what each costs, file pins as their files are now.
    let pins<'K>
        (asks: ContextAsks)
        (transcript: Eff<'K, Transcript>)
        (size: string -> LineRange list -> Eff<'K, int option>)
        : HostBinding<'K> =
        HostBinding.create0
            "context.pins"
            "What is pinned (context.pin, context.pinFile) and what each pin costs at every call."
            (Codec.list pinInfo)
            (fun () ->
                eff {
                    let! t = transcript
                    let infos = ResizeArray<PinInfo>()

                    for spec in current t asks.Pending do
                        match spec.Pin with
                        | Pin.Value pieces ->
                            let chars = piecesChars pieces

                            infos.Add
                                {
                                    Name = spec.Name
                                    Kind = "value"
                                    Path = None
                                    Lines = None
                                    Chars = Some chars
                                    Tokens = Some(Tokens.ofChars chars)
                                }
                        | Pin.File(path, lines) ->
                            let! chars = size path lines

                            infos.Add
                                {
                                    Name = spec.Name
                                    Kind = "file"
                                    Path = Some path
                                    Lines = lines |> List.tryHead |> Option.map (fun r -> r.First, r.Last)
                                    Chars = chars
                                    Tokens = chars |> Option.map Tokens.ofChars
                                }

                    return Ok(List.ofSeq infos)
                })
