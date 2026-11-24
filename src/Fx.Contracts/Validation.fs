namespace Fx.Contracts

/// A result that accumulates every error instead of stopping at the first one. Combine
/// independent validations with `Validation.map2`, `Validation.traverse` or `and!` in
/// `validation { }` to collect all their errors; `let!` (bind) stops at the first failure.
type Validation<'T, 'E> = Result<'T, 'E list>

[<RequireQualifiedAccess>]
module Validation =

    let ok (value: 'T) : Validation<'T, 'E> = Ok value

    let error (error: 'E) : Validation<'T, 'E> = Error [ error ]

    let errors (errors: 'E list) : Validation<'T, 'E> = Error errors

    let ofResult (result: Result<'T, 'E>) : Validation<'T, 'E> = Result.mapError List.singleton result

    let ofOption (error: 'E) (option: 'T option) : Validation<'T, 'E> =
        match option with
        | Some value -> Ok value
        | None -> Error [ error ]

    let map (f: 'T -> 'U) (validation: Validation<'T, 'E>) : Validation<'U, 'E> = Result.map f validation

    /// Maps every error.
    let mapErrors (f: 'E -> 'F) (validation: Validation<'T, 'E>) : Validation<'T, 'F> =
        Result.mapError (List.map f) validation

    /// Sequential: `f` runs only on success, so errors do not accumulate across a bind.
    let bind (f: 'T -> Validation<'U, 'E>) (validation: Validation<'T, 'E>) : Validation<'U, 'E> =
        Result.bind f validation

    /// Combines two independent validations, keeping the errors of both.
    let map2 (f: 'A -> 'B -> 'C) (a: Validation<'A, 'E>) (b: Validation<'B, 'E>) : Validation<'C, 'E> =
        match a, b with
        | Ok a, Ok b -> Ok(f a b)
        | Error e1, Error e2 -> Error(e1 @ e2)
        | Error e, Ok _
        | Ok _, Error e -> Error e

    let map3
        (f: 'A -> 'B -> 'C -> 'D)
        (a: Validation<'A, 'E>)
        (b: Validation<'B, 'E>)
        (c: Validation<'C, 'E>)
        : Validation<'D, 'E> =
        map2 (fun f c -> f c) (map2 f a b) c

    /// Applies a validated function to a validated argument, keeping the errors of both.
    let apply (f: Validation<'T -> 'U, 'E>) (validation: Validation<'T, 'E>) : Validation<'U, 'E> =
        map2 (fun f x -> f x) f validation

    let zip (a: Validation<'A, 'E>) (b: Validation<'B, 'E>) : Validation<'A * 'B, 'E> = map2 (fun a b -> a, b) a b

    /// Validates every item with `f` (which also gets the item's index) and keeps all errors, in order.
    let traverseIndexed (f: int -> 'T -> Validation<'U, 'E>) (items: 'T list) : Validation<'U list, 'E> =
        let values = ResizeArray<'U>()
        let errors = ResizeArray<'E>()

        items
        |> List.iteri (fun index item ->
            match f index item with
            | Ok value -> values.Add value
            | Error es -> errors.AddRange es)

        if errors.Count = 0 then
            Ok(List.ofSeq values)
        else
            Error(List.ofSeq errors)

    /// Validates every item and keeps all errors, in order.
    let traverse (f: 'T -> Validation<'U, 'E>) (items: 'T list) : Validation<'U list, 'E> =
        traverseIndexed (fun _ item -> f item) items

    let sequence (validations: Validation<'T, 'E> list) : Validation<'T list, 'E> = traverse id validations

/// The `validation { }` builder: `and!` accumulates errors, `let!` stops at the first failure.
type ValidationBuilder() =
    member _.Return(value: 'T) : Validation<'T, 'E> = Ok value
    member _.ReturnFrom(validation: Validation<'T, 'E>) : Validation<'T, 'E> = validation

    member _.Bind(validation: Validation<'T, 'E>, f: 'T -> Validation<'U, 'E>) : Validation<'U, 'E> =
        Result.bind f validation

    member _.BindReturn(validation: Validation<'T, 'E>, f: 'T -> 'U) : Validation<'U, 'E> = Result.map f validation

    member _.MergeSources(a: Validation<'A, 'E>, b: Validation<'B, 'E>) : Validation<'A * 'B, 'E> = Validation.zip a b

[<AutoOpen>]
module ValidationBuilderInstance =
    /// Builds a `Validation`: `and!` accumulates errors, `let!` stops at the first failure.
    let validation = ValidationBuilder()
