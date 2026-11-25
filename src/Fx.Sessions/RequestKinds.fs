namespace Fx.Sessions

open Fx.Contracts

/// A kind of pending request (spec §5): what its payload and its answers look like, and which client
/// capabilities can answer it. Typed: the opener writes a `'Payload`, the answering client a `'Answer`.
/// Decision cards (T301), grant requests (T123), workflow gates (T306), auth flows (T121) and
/// installs are kinds their owners define; extensions contribute theirs.
type RequestKind<'Payload, 'Answer> =
    {
        /// Stable and dotted, e.g. `confirm`, `grant.request`.
        Id: string
        Description: string
        Payload: Codec<'Payload>
        Answer: Codec<'Answer>
        /// An answer that decodes may still not fit the payload (a choice that is not an option).
        Check: 'Payload -> 'Answer -> Result<unit, string>
        /// The client capabilities that can answer it: the kind's id unless the kind says otherwise.
        Capabilities: string list
    }

    override this.ToString() = "RequestKind(" + this.Id + ")"

/// A request kind with its types erased: what the session machine checks requests and answers with.
type RequestKindInfo =
    {
        Id: string
        Description: string
        PayloadSchema: Schema
        AnswerSchema: Schema
        Capabilities: string list
        /// The issues of a payload; none if it is valid.
        CheckPayload: Json -> Issue list
        /// The issues of an answer to a payload; none if it is valid.
        CheckAnswer: Json -> Json -> Issue list
    }

    override this.ToString() = "RequestKindInfo(" + this.Id + ")"

module RequestKind =
    /// A kind answerable by clients that offer its id as a capability, with no check beyond the codecs.
    let create
        (id: string)
        (description: string)
        (payload: Codec<'Payload>)
        (answer: Codec<'Answer>)
        : RequestKind<'Payload, 'Answer> =
        if System.String.IsNullOrWhiteSpace id then
            invalidArg (nameof id) "A request kind needs an id."

        {
            Id = id
            Description = description
            Payload = payload
            Answer = answer
            Check = fun _ _ -> Ok()
            Capabilities = [ id ]
        }

    let withCheck
        (check: 'Payload -> 'Answer -> Result<unit, string>)
        (kind: RequestKind<'Payload, 'Answer>)
        : RequestKind<'Payload, 'Answer> =
        { kind with Check = check }

    /// The client capabilities that can answer requests of this kind, instead of its id.
    let answerableBy
        (capabilities: string list)
        (kind: RequestKind<'Payload, 'Answer>)
        : RequestKind<'Payload, 'Answer> =
        if List.isEmpty capabilities then
            invalidArg (nameof capabilities) "Some client capability must be able to answer the kind."

        { kind with
            Capabilities = capabilities
        }

    let info (kind: RequestKind<'Payload, 'Answer>) : RequestKindInfo =
        let decodePayload (json: Json) = kind.Payload.Decode json

        {
            Id = kind.Id
            Description = kind.Description
            PayloadSchema = kind.Payload.Schema
            AnswerSchema = kind.Answer.Schema
            Capabilities = kind.Capabilities
            CheckPayload =
                fun json ->
                    match decodePayload json with
                    | Ok _ -> []
                    | Error issues -> issues
            CheckAnswer =
                fun payload answer ->
                    match decodePayload payload, kind.Answer.Decode answer with
                    | Ok payload, Ok answer ->
                        match kind.Check payload answer with
                        | Ok() -> []
                        | Error message -> [ Issue.create message ]
                    | Error _, _ -> [ Issue.create "the request's payload does not decode" ]
                    | _, Error issues -> issues
        }

    /// A request of this kind, with a typed payload.
    let request
        (kind: RequestKind<'Payload, 'Answer>)
        (id: string)
        (title: string)
        (origin: RequestOrigin)
        (reply: Reply)
        (lifetime: Lifetime)
        (payload: 'Payload)
        : RequestSpec =
        {
            Id = id
            Kind = kind.Id
            Title = title
            Payload = Json.canonicalize (kind.Payload.Encode payload)
            Origin = origin
            Reply = reply
            Lifetime = lifetime
            Expiry = None
        }

    /// An answer of this kind, as a client sends it.
    let answer (kind: RequestKind<'Payload, 'Answer>) (answer: 'Answer) : Json =
        Json.canonicalize (kind.Answer.Encode answer)

    /// The typed answer in a resolution, if it carries one (an answer, or an expiry's default).
    let response (kind: RequestKind<'Payload, 'Answer>) (resolution: Resolution) : Validation<'Answer option, Issue> =
        match resolution with
        | Resolution.Answered json
        | Resolution.Expired(Some json) -> kind.Answer.Decode json |> Result.map Some
        | Resolution.Cancelled _
        | Resolution.Expired None -> Ok None

/// The request kinds a session knows, by id.
type RequestKinds =
    {
        Kinds: Map<string, RequestKindInfo>
    }

    override this.ToString() =
        "RequestKinds("
        + String.concat ", " (this.Kinds |> Map.toList |> List.map fst)
        + ")"

/// The payload of a `confirm` request.
type Confirmation =
    {
        Message: string
    }

    override this.ToString() = this.Message

/// The payload of a `choice` request.
type Choice =
    {
        Question: string
        Options: string list
    }

    override this.ToString() =
        this.Question + " [" + String.concat " | " this.Options + "]"

module RequestKinds =
    let empty: RequestKinds = { Kinds = Map.empty }

    /// The kinds, by id. Two kinds with one id are a defect.
    let create (kinds: RequestKindInfo list) : RequestKinds =
        kinds
        |> List.fold
            (fun (registry: RequestKinds) kind ->
                if registry.Kinds.ContainsKey kind.Id then
                    invalidArg (nameof kinds) ("Two request kinds have the id " + Json.quote kind.Id + ".")

                {
                    Kinds = Map.add kind.Id kind registry.Kinds
                })
            empty

    let tryFind (id: string) (kinds: RequestKinds) : RequestKindInfo option = Map.tryFind id kinds.Kinds

    /// A yes-or-no question: `{"message"}`, answered `true` or `false`.
    let confirm: RequestKind<Confirmation, bool> =
        let payload =
            Codec.record {
                let! message =
                    Codec.field "message" (fun (c: Confirmation) -> c.Message) (Codec.string |> Codec.nonEmpty)

                return { Message = message }
            }
            |> Codec.describe "A yes-or-no question."
            |> Codec.named "Confirmation"

        RequestKind.create "confirm" "A yes-or-no question for a person." payload Codec.bool

    /// A choice among options: `{"question", "options"}`, answered with one of the options.
    let choice: RequestKind<Choice, string> =
        let payload =
            Codec.record {
                let! question =
                    Codec.field "question" (fun (c: Choice) -> c.Question) (Codec.string |> Codec.nonEmpty)

                and! options =
                    Codec.field "options" (fun c -> c.Options) (Codec.list Codec.string |> Codec.minItems 1)

                return
                    {
                        Question = question
                        Options = options
                    }
            }
            |> Codec.describe "A choice among options."
            |> Codec.named "Choice"

        RequestKind.create "choice" "A choice among options, for a person." payload Codec.string
        |> RequestKind.withCheck (fun choice answer ->
            if List.contains answer choice.Options then
                Ok()
            else
                Error("expected one of " + String.concat ", " (choice.Options |> List.map Json.quote)))

    /// The kinds the core defines.
    let builtIn: RequestKinds =
        create [ RequestKind.info confirm; RequestKind.info choice ]
