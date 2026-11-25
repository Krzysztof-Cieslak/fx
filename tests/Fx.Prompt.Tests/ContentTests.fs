/// Pieces, blocks, paths, line ranges, content exclusions and `SeenRanges`.
module Fx.Prompt.Tests.ContentTests

open Expecto
open Fx.Contracts
open Fx.Models
open Fx.Prompt
open Fx.Prompt.Tests.Support

let private range (a: int) (b: int) = LineRange.create a b

let private excludes (patterns: string list) (path: string) =
    Exclusions.excludes (exclusionsOf patterns) path

[<Tests>]
let content =
    testList
        "Content"
        [
            test "paths are workspace-relative and /-separated" {
                Expect.equal (Paths.normalize "src\\a\\b.fs") "src/a/b.fs" "backslashes"
                Expect.equal (Paths.normalize "./src//a/./b.fs/") "src/a/b.fs" "dots and repeats"
                Expect.equal (Paths.normalize "/src/a.fs") "src/a.fs" "a leading separator"
                Expect.equal (Paths.normalize "../x/a.fs") "../x/a.fs" "never resolves .."
            }

            test "line ranges merge when they overlap or touch" {
                Expect.equal
                    (LineRange.normalize [ range 10 20; range 1 3; range 4 6; range 15 30; range 40 41 ])
                    [ range 1 6; range 10 30; range 40 41 ]
                    "merged and sorted"

                Expect.equal (LineRange.normalize [ range 5 4; range 0 2 ]) [ range 1 2 ] "invalid ones dropped"
                Expect.equal (string (range 3 3)) "3" "one line"
            }

            test "output keeps its head and tail within the limit" {
                let text = String.replicate 500 "a" + "MIDDLE" + String.replicate 500 "z"

                match Piece.output 200 text with
                | Piece.Text cut ->
                    Expect.isLessThanOrEqual cut.Length 200 "within the limit"
                    Expect.stringStarts cut "aaaa" "the head"
                    Expect.stringEnds cut "zzzz" "the tail"
                    Expect.stringContains cut "characters omitted" "says what was left out"
                    Expect.isFalse (cut.Contains "MIDDLE") "the middle is gone"
                | other -> failtest (string other)

                Expect.equal (Piece.output 200 "short") (Piece.Text "short") "short text as is"

                // Never splits a surrogate pair.
                let emoji = String.replicate 300 "😀"

                match Piece.output 100 emoji with
                | Piece.Text cut ->
                    for i in 0 .. cut.Length - 1 do
                        if System.Char.IsHighSurrogate cut[i] then
                            Expect.isTrue (i + 1 < cut.Length && System.Char.IsLowSurrogate cut[i + 1]) "a whole pair"
                | other -> failtest (string other)
            }

            test "pieces join into text parts, media apart" {
                let image: MediaPart =
                    {
                        Kind = MediaKind.Image
                        MediaType = "image/png"
                        Source = MediaSource.Base64 "AAAA"
                        Name = Some "shot.png"
                    }

                let parts =
                    Piece.parts
                        [
                            Piece.Text "one"
                            Piece.File(file "src/a.fs" [ 2, 3 ] "two\nthree")
                            Piece.Media image
                            Piece.Withheld "secrets/key"
                            Piece.Text ""
                        ]

                Expect.equal
                    parts
                    [
                        Part.text ("one\nsrc/a.fs:2-3 (" + (digestOf "src/a.fs").Substring(0, 19) + ")\ntwo\nthree")
                        Part.Media image
                        Part.text "[secrets/key: withheld by content exclusions]"
                    ]
                    "joined"

                Expect.equal (Piece.parts []) [] "nothing"
            }

            test "references show their kind, name, type and preview" {
                let reference =
                    Piece.Ref
                        {
                            Kind = "handle"
                            Id = "h1"
                            Type = Some "Match[]"
                            Preview = Preview.full "Match[](len=37)"
                        }

                Expect.equal (Piece.render reference) "[handle h1: Match[]] Match[](len=37)" "rendered"
            }

            test "pieces and blocks survive their JSON" {
                let block: Block =
                    {
                        Id = "pin:x"
                        Version = Some "v1"
                        Pieces =
                            [
                                Piece.Text "a"
                                Piece.File(file "a.fs" [] "x")
                                Piece.Withheld "b"
                                Piece.Ref
                                    {
                                        Kind = "blob"
                                        Id = "sha256:00"
                                        Type = None
                                        Preview = { Text = "…"; Truncated = true }
                                    }
                            ]
                    }

                Expect.equal (Codec.decodeText Block.codec (Codec.encodeText Block.codec block)) (Ok block) "round trip"
            }
        ]

[<Tests>]
let exclusions =
    testList
        "Content exclusions"
        [
            test "a name without a slash matches at any depth" {
                Expect.isTrue (excludes [ ".env" ] ".env") "at the root"
                Expect.isTrue (excludes [ ".env" ] "app/config/.env") "deep"
                Expect.isFalse (excludes [ ".env" ] "app/.envrc") "not a prefix"
                Expect.isTrue (excludes [ "*.pem" ] "certs/server.pem") "a glob"
            }

            test "a slash anchors at the workspace root" {
                Expect.isTrue (excludes [ "/secrets" ] "secrets/a.txt") "under an anchored directory"
                Expect.isFalse (excludes [ "/secrets" ] "app/secrets/a.txt") "not elsewhere"
                Expect.isTrue (excludes [ "config/prod.json" ] "config/prod.json") "a path"
                Expect.isFalse (excludes [ "config/prod.json" ] "x/config/prod.json") "anchored"
            }

            test "** spans segments; a directory excludes what is under it" {
                Expect.isTrue (excludes [ "**/keys/*.json" ] "a/b/keys/k.json") "deep"
                Expect.isTrue (excludes [ "**/keys/*.json" ] "keys/k.json") "zero segments"
                Expect.isTrue (excludes [ "vault/**" ] "vault/a/b/c") "everything under"
                Expect.isTrue (excludes [ "private" ] "src/private/deep/file.fs") "a matched directory"
                Expect.isFalse (excludes [ "build/" ] "build") "a directory pattern does not match a file"
                Expect.isTrue (excludes [ "build/" ] "build/out.txt") "but what is under it"
            }

            test "?, classes and backslashes" {
                Expect.isTrue (excludes [ "key?.txt" ] "key1.txt") "?"
                Expect.isFalse (excludes [ "key?.txt" ] "key10.txt") "one character"
                Expect.isTrue (excludes [ "id_[rd]sa" ] ".ssh/id_rsa") "a class"
                Expect.isFalse (excludes [ "id_[!rd]sa" ] ".ssh/id_rsa") "a negated class"
                Expect.isTrue (excludes [ "log[0-9]" ] "log7") "a range"
                Expect.isTrue (excludes [ "a[b" ] "a[b") "an unclosed class is literal"
                Expect.isTrue (excludes [ "keys\\*.pem" ] "keys/a.pem") "a backslash separates"
            }

            test "matching ignores case and separators" {
                Expect.isTrue (excludes [ "Secrets/" ] "secrets\\API.key") "case and backslashes"
                Expect.isTrue (excludes [ "*.ENV" ] "prod.env") "case of a glob"
            }

            test "invalid patterns are errors, not ignored" {
                Expect.isError (Exclusions.create [ "ok"; "" ]) "empty"
                Expect.isError (Exclusions.create [ "!keep.txt" ]) "a negation"
                Expect.isError (Exclusions.create [ "/" ]) "no path"
                Expect.isOk (Exclusions.create []) "none"
            }

            test "filtering withholds excluded files only" {
                let ex = exclusionsOf [ "secret*" ]

                let pieces, changed =
                    Exclusions.filter
                        ex
                        [
                            Piece.File(file "src/a.fs" [] "fine")
                            Piece.File(file "secret.txt" [] "TOP")
                            Piece.Text "secret.txt mentioned in text"
                        ]

                Expect.isTrue changed "changed"

                Expect.equal
                    pieces
                    [
                        Piece.File(file "src/a.fs" [] "fine")
                        Piece.Withheld "secret.txt"
                        Piece.Text "secret.txt mentioned in text"
                    ]
                    "withheld"

                let same = [ Piece.Text "x" ]
                Expect.isFalse (snd (Exclusions.filter ex same)) "unchanged"
            }
        ]

[<Tests>]
let seenRanges =
    testList
        "SeenRanges"
        [
            test "ranges of one version merge; the whole file covers all" {
                let a = file "src/a.fs" [ 10, 20 ] ""
                let b = file "src/a.fs" [ 21, 30; 50, 60 ] ""

                let seen = SeenRanges.empty |> SeenRanges.add a |> SeenRanges.add b

                Expect.equal
                    (SeenRanges.tryFind "src/a.fs" a.Digest seen)
                    (Some
                        {
                            Whole = false
                            Lines = [ range 10 30; range 50 60 ]
                        })
                    "merged"

                Expect.isTrue (SeenRanges.covers "src/a.fs" a.Digest (range 12 28) seen) "covered"
                Expect.isFalse (SeenRanges.covers "src/a.fs" a.Digest (range 25 55) seen) "a gap"

                Expect.equal
                    (SeenRanges.unseen "src/a.fs" a.Digest (range 1 70) seen)
                    [ range 1 9; range 31 49; range 61 70 ]
                    "the gaps"

                let whole = seen |> SeenRanges.add (file "src/a.fs" [] "")
                Expect.equal (SeenRanges.unseen "src/a.fs" a.Digest (range 1 999) whole) [] "the whole file"
            }

            test "versions never mix, paths are normalized" {
                let v1 = file "src/a.fs" [ 1, 5 ] ""
                let v2 = { v1 with Digest = "sha256:other" }
                let seen = SeenRanges.empty |> SeenRanges.add v1

                Expect.isFalse (SeenRanges.covers "src/a.fs" v2.Digest (range 1 5) seen) "another version"
                Expect.isTrue (SeenRanges.covers ".\\src\\a.fs" v1.Digest (range 2 3) seen) "normalized"
                Expect.equal (SeenRanges.versions "src/a.fs" seen) [ v1.Digest ] "versions"

                Expect.equal
                    (SeenRanges.unseen "src/b.fs" "sha256:x" (range 3 4) seen)
                    [ range 3 4 ]
                    "a file never seen"
            }

            test "union and JSON" {
                let a = SeenRanges.empty |> SeenRanges.add (file "a.fs" [ 1, 2 ] "")

                let b =
                    SeenRanges.empty
                    |> SeenRanges.add (file "a.fs" [ 3, 4 ] "")
                    |> SeenRanges.add (file "b.fs" [] "")

                let both = SeenRanges.union a b

                Expect.isTrue (SeenRanges.covers "a.fs" (digestOf "a.fs") (range 1 4) both) "merged"
                Expect.isTrue (SeenRanges.covers "b.fs" (digestOf "b.fs") (range 1 100) both) "whole"

                Expect.equal
                    (Codec.decodeText SeenRanges.codec (Codec.encodeText SeenRanges.codec both))
                    (Ok both)
                    "round trip"
            }
        ]
