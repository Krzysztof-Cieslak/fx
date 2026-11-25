/// Declaration files: layouts, members, determinism and invalid input.
module Fx.Contracts.TypeScript.Tests.FileTests

open System
open Expecto
open Fx
open Fx.Contracts
open Fx.Contracts.TypeScript

let private lines (text: string list) = String.Join("\n", text) + "\n"

let private header = DeclarationFile.Header + "\n\n"

let private point =
    Codec.record {
        let! x = Codec.field "x" fst Codec.float
        and! y = Codec.field "y" snd Codec.float
        return x, y
    }
    |> Codec.named "Point"

let private move: Method =
    Method.create "move" point.Schema
    |> Method.describe "Moves the cursor."
    |> Method.withParameter (Parameter.required "to" point.Schema |> Parameter.describe "Where to.")

let private file (layout: Layout) (types: Schema list) (members: Member list) : DeclarationFile =
    {
        Layout = layout
        Types = types
        Members = members
    }

let private throws (name: string) (render: unit -> string) =
    Expect.throwsT<ArgumentException> (render >> ignore) name

[<Tests>]
let tests =
    testList
        "Declaration files"
        [
            test "an empty module stays a module" {
                Expect.equal
                    (DeclarationFile.render (DeclarationFile.empty Layout.Module))
                    (header + "export {};\n")
                    "module"
            }

            test "a module exports its members and types" {
                Expect.equal
                    (DeclarationFile.render (file Layout.Module [] [ Member.Method move ]))
                    (header
                     + lines
                         [
                             "/**"
                             " * Moves the cursor."
                             " *"
                             " * @param to Where to."
                             " */"
                             "export declare function move(to: Point): Promise<Point>;"
                             ""
                             "export interface Point {"
                             "  x: number;"
                             "  y: number;"
                             "}"
                         ])
                    "module"
            }

            test "a global script declares everything in the global's namespace" {
                let name = Product.kernelGlobal

                Expect.equal
                    (DeclarationFile.render (
                        file
                            (Layout.Global(name, Some "The host bridge."))
                            [ point.Schema ]
                            (Member.ofPaths [ "cursor", "The cursor." ] [ { move with Name = "cursor.move" } ])
                    ))
                    (header
                     + lines
                         [
                             "/** The host bridge. */"
                             $"declare namespace {name} {{"
                             "  /** The cursor. */"
                             "  const cursor: {"
                             "    /**"
                             "     * Moves the cursor."
                             "     *"
                             "     * @param to Where to."
                             "     */"
                             "    move(to: Point): Promise<Point>;"
                             "  };"
                             ""
                             "  interface Point {"
                             "    x: number;"
                             "    y: number;"
                             "  }"
                             "}"
                         ])
                    "global"
            }

            test "members nest by dotted path, sorted by name" {
                let leaf name = Method.create name Codec.unit.Schema

                let members = Member.ofPaths [] [ leaf "b.y"; leaf "a"; leaf "b.x"; leaf "b.c.z" ]

                Expect.equal
                    (DeclarationFile.render (file Layout.Module [] members))
                    (header
                     + lines
                         [
                             "export declare function a(): Promise<null>;"
                             ""
                             "export declare const b: {"
                             "  c: {"
                             "    z(): Promise<null>;"
                             "  };"
                             "  x(): Promise<null>;"
                             "  y(): Promise<null>;"
                             "};"
                         ])
                    "nested"

                Expect.throwsT<ArgumentException>
                    (fun () ->
                        DeclarationFile.render (file Layout.Module [] (Member.ofPaths [] [ leaf "c-d.e" ]))
                        |> ignore)
                    "c-d is not an identifier at the top level"
            }

            test "parameters: optional ones, names and long signatures" {
                let schema = Codec.string.Schema

                let wide =
                    Method.create "search" (Codec.list Codec.string).Schema
                    |> Method.withParameter (Parameter.optional "scope" schema)
                    |> Method.withParameter (Parameter.required "class" schema)
                    |> Method.withParameter (Parameter.required "class" schema)
                    |> Method.withParameter (Parameter.optional "a-very-long-parameter-name" schema)
                    |> Method.withParameter (Parameter.optional "options" (Codec.stringMap Codec.json).Schema)

                Expect.equal
                    (DeclarationFile.render (file Layout.Module [] [ Member.Method wide ]))
                    (header
                     + lines
                         [
                             "export declare function search("
                             "  scope: string | undefined,"
                             "  class_: string,"
                             "  class_2: string,"
                             "  a_very_long_parameter_name?: string,"
                             "  options?: { [key: string]: unknown },"
                             "): Promise<string[]>;"
                         ])
                    "parameters"
            }

            test "multi-line parameter and return types" {
                let input =
                    Codec.record {
                        let! a = Codec.field "a" fst (Codec.string |> Codec.describe "A.")
                        and! b = Codec.field "b" snd Codec.bool
                        return a, b
                    }

                let ask =
                    Method.create "ask" input.Schema
                    |> Method.withParameter (Parameter.required "input" input.Schema)

                Expect.equal
                    (DeclarationFile.render (file Layout.Module [] [ Member.Method ask ]))
                    (header
                     + lines
                         [
                             "export declare function ask("
                             "  input: {"
                             "    /** A. */"
                             "    a: string;"
                             "    b: boolean;"
                             "  },"
                             "): Promise<{"
                             "  /** A. */"
                             "  a: string;"
                             "  b: boolean;"
                             "}>;"
                         ])
                    "layout"
            }

            test "the text does not depend on the order of types and members" {
                let a = Method.create "x.a" Codec.unit.Schema
                let b = Method.create "x.b" point.Schema
                let c = Method.create "c" Codec.unit.Schema
                let types = [ Samples.span.Schema; Samples.priority.Schema; Samples.tree.Schema ]

                let render (types: Schema list) (methods: Method list) =
                    DeclarationFile.render (file (Layout.Global("g", None)) types (Member.ofPaths [] methods))

                let expected = render types [ a; b; c ]
                Expect.equal (render (List.rev types) [ c; b; a ]) expected "reversed"
                Expect.equal (render [ types[1]; types[2]; types[0] ] [ b; c; a ]) expected "shuffled"
            }

            test "invalid input is a defect" {
                let unnamed = Codec.string.Schema
                throws "unnamed type" (fun () -> DeclarationFile.render (file Layout.Module [ unnamed ] []))

                let dangling =
                    Schema.ofKind (SchemaKind.Map(Schema.ofKind (SchemaKind.Ref "Nowhere")))

                throws "dangling reference" (fun () ->
                    DeclarationFile.render (
                        file Layout.Module [ Schema.ofKind (SchemaKind.Named { Name = "D"; Body = dangling }) ] []
                    ))

                let other = Codec.int |> Codec.named "Point"

                throws "conflicting names" (fun () ->
                    DeclarationFile.render (file Layout.Module [ point.Schema; other.Schema ] []))

                throws "conflict through a member" (fun () ->
                    DeclarationFile.render (
                        file Layout.Module [ point.Schema ] [ Member.Method(Method.create "f" other.Schema) ]
                    ))

                throws "duplicate members" (fun () ->
                    DeclarationFile.render (file Layout.Module [] [ Member.Method move; Member.Method move ]))

                throws "reserved top-level name" (fun () ->
                    DeclarationFile.render (file Layout.Module [] [ Member.Method { move with Name = "delete" } ]))

                throws "invalid global" (fun () ->
                    DeclarationFile.render (DeclarationFile.empty (Layout.Global("my-global", None))))

                throws "reserved global" (fun () ->
                    DeclarationFile.render (DeclarationFile.empty (Layout.Global("class", None))))
            }

            test "dotted paths that do not form groups are a defect" {
                let leaf name = Method.create name Codec.unit.Schema

                Expect.throwsT<ArgumentException>
                    (fun () -> Member.ofPaths [] [ leaf "a"; leaf "a.b" ] |> ignore)
                    "method and group"

                Expect.throwsT<ArgumentException>
                    (fun () -> Member.ofPaths [] [ leaf "a.b"; leaf "a.b" ] |> ignore)
                    "twice"

                Expect.throwsT<ArgumentException>
                    (fun () -> Member.ofPaths [] [ leaf "a..b" ] |> ignore)
                    "empty segment"

                Expect.throwsT<ArgumentException> (fun () -> Member.ofPaths [] [ leaf "" ] |> ignore) "empty path"

                Expect.throwsT<ArgumentException>
                    (fun () -> Member.ofPaths [ "a.b", "x" ] [ leaf "a.b" ] |> ignore)
                    "a method is not a group"
            }

            test "the samples generate the same text twice" {
                Expect.equal
                    (DeclarationFile.render Samples.kernel)
                    (DeclarationFile.render Samples.kernel)
                    "deterministic"
            }
        ]
