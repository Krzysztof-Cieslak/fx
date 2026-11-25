module Fx.Models.Tests.ExclusionTests

open System
open System.Text.RegularExpressions
open Expecto
open FsCheck
open FsCheck.FSharp
open Fx.Contracts
open Fx.Models

/// 300 tests per property; set PROPERTY_TESTS to run more.
let private config =
    { FsCheckConfig.defaultConfig with
        maxTest =
            match Environment.GetEnvironmentVariable "PROPERTY_TESTS" with
            | null -> 300
            | count -> int count
    }

let private glob (pattern: string) : Glob =
    match Glob.parse pattern with
    | Ok(Some glob) -> glob
    | Ok None -> failtest $"{pattern} is blank"
    | Error e -> failtest $"{pattern}: {e}"

let private exclusions (lists: (string * string list) list) : Exclusions =
    match
        Exclusions.create (
            lists
            |> List.map (fun (origin, patterns) -> { Origin = origin; Patterns = patterns })
        )
    with
    | Ok e -> e
    | Error issues -> failtest (String.Join("; ", issues |> List.map string))

let private excludedBy (patterns: string list) (path: string) : bool =
    Exclusions.excludes (exclusions [ "test", patterns ]) path

/// Cases: patterns, then paths excluded and paths not.
let private table: (string list * string list * string list) list =
    [
        [ "*.env" ], [ ".env"; "a/.env"; "a/b/prod.env"; "x.env" ], [ "a/env"; "x.envy"; "env" ]
        [ "/secrets/" ], [ "secrets/x"; "secrets/a/b" ], [ "a/secrets/x"; "secrets"; "secretsx/y" ]
        [ "secrets/" ], [ "secrets/x"; "a/secrets/x"; "a/b/secrets/c/d" ], [ "secrets"; "a/secrets" ]
        [ "secrets" ], [ "secrets"; "a/secrets"; "secrets/x"; "a/secrets/b" ], [ "secretsx"; "a/xsecrets" ]
        [ "docs/*.md" ], [ "docs/a.md"; "docs/.md" ], [ "docs/x/a.md"; "a/docs/a.md"; "docs/a.mdx" ]
        [ "/docs/*.md" ], [ "docs/a.md" ], [ "a/docs/a.md" ]
        [ "a/**/b" ], [ "a/b"; "a/x/b"; "a/x/y/b"; "a/b/c" ], [ "x/a/b"; "a/xb"; "ab" ]
        [ "**/build/**" ], [ "build/x"; "a/build/x/y" ], [ "build"; "rebuild/x" ]
        [ "logs/**" ], [ "logs/a"; "logs/a/b" ], [ "logs"; "x/logs/a" ]
        [ "**" ], [ "a"; "a/b"; ".x" ], []
        [ "**/" ], [ "a/b"; "a/b/c" ], [ "a" ]
        [ "**/*.pem" ], [ "k.pem"; "a/b/k.pem" ], [ "k.pem.txt" ]
        [ "*.{pem,key}" ], [ "a.pem"; "x/a.key" ], [ "a.crt"; "a.pemkey" ]
        [ "{cert,key}s/" ], [ "certs/a"; "x/keys/b" ], [ "certs"; "ks/a" ]
        [ "id_{rsa,ed25519}{,.pub}" ], [ "id_rsa"; "id_rsa.pub"; "h/.ssh/id_ed25519" ], [ "id_dsa"; "id_rsa.pem" ]
        [ "[a-c].txt" ], [ "a.txt"; "x/c.txt" ], [ "d.txt"; "ab.txt" ]
        [ "[!a-c].txt" ], [ "d.txt"; "z.txt" ], [ "a.txt"; "/.txt" ]
        [ "[]x].txt" ], [ "].txt"; "x.txt" ], [ "y.txt" ]
        [ "?.cfg" ], [ "a.cfg"; "d/b.cfg" ], [ "ab.cfg"; ".cfg" ]
        [ "a?c" ], [ "abc" ], [ "a/c" ]
        [ "a*c" ], [ "ac"; "abbc" ], [ "a/c"; "ab/c" ]
        [ "a**c" ], [ "ac"; "abc" ], [ "a/b/c" ]
        [ "[*].txt" ], [ "*.txt" ], [ "a.txt" ]
        [ "[#]notes" ], [ "#notes" ], [ "notes" ]
        [ "a!b" ], [ "a!b" ], [ "ab" ]
        [ "a[b" ], [ "a[b"; "x/A[B" ], [ "ab" ]
        [ "x{y" ], [ "x{y" ], [ "xy" ]
        [ @"keys\*.pem" ], [ "keys/a.pem"; @"keys\b.pem" ], [ "x/keys/a.pem" ]
        [ @"\secrets" ], [ "secrets/a" ], [ "a/secrets/b" ]
        [ "./a/./b" ], [ "a/b" ], [ "x/a/b" ]
        [ "# a comment"; "real" ], [ "real" ], [ "# a comment"; "a" ]
        [ "Secrets/*.ENV" ], [ "secrets/prod.env"; @"SECRETS\X.Env" ], [ "secret/prod.env" ]
        [ "[a-c]x" ], [ "Bx"; "ax" ], [ "dx" ]
        [ "  padded.txt  " ], [ "padded.txt" ], [ "  padded.txt  "; "padded" ]
        [ "/a/b" ], [ "a/b"; "a/b/c" ], [ "x/a/b" ]
        [ ".git" ], [ ".git"; ".git/config"; "sub/.git/HEAD" ], [ ".gitignore" ]
        [ "été/*" ], [ "été/a" ], [ "ete/a" ]
    ]

/// An independent model of the patterns the generator makes (segments of `a`, `b`, `*`, `?`, `**`
/// joined by `/`), as a regular expression.
let private oracle (pattern: string) : Regex =
    let body = pattern.TrimStart '/'
    let anchored = pattern.StartsWith '/' || body.Contains '/'
    let segments = body.Split '/'

    let globstar (s: string) =
        s.Length >= 2 && s |> Seq.forall (fun c -> c = '*')

    let segment (s: string) =
        if globstar s then
            None
        else
            let collapsed = Regex.Replace(s, @"\*+", "*")

            collapsed
            |> Seq.map (fun c ->
                match c with
                | '*' -> "[^/]*"
                | '?' -> "[^/]"
                | c -> Regex.Escape(string c))
            |> String.concat ""
            |> Some

    let parts =
        segments
        |> Array.mapi (fun i s ->
            let last = i = segments.Length - 1

            match segment s with
            | Some re -> if last then re else re + "/"
            | None when segments.Length = 1 -> ".+"
            | None when last -> ".+"
            | None -> "(?:.*/)?")
        |> String.concat ""

    let prefix = if anchored || globstar segments[0] then "" else "(?:.*/)?"

    Regex("^" + prefix + parts + "$", RegexOptions.CultureInvariant)

let private patternGen: Gen<string> =
    gen {
        let! count = Gen.choose (1, 4)

        let! segments =
            Gen.listOfLength count (Gen.elements [ "a"; "b"; "ab"; "*"; "?"; "**"; "a*"; "*b"; "a?"; "***" ])

        let! leading = Gen.elements [ true; false; false ]
        let body = String.Join("/", segments)
        return if leading then "/" + body else body
    }

let private pathGen: Gen<string> =
    gen {
        let! count = Gen.choose (1, 5)

        let! segments =
            Gen.listOfLength count (Gen.elements [ "a"; "b"; "ab"; "ba"; "aa"; "bb"; "abb" ])

        return String.Join("/", segments)
    }

/// Paths written every way a producer might: mixed separators, `.` segments, repeated separators.
let private messyPathGen: Gen<string * string> =
    gen {
        let! count = Gen.choose (1, 5)

        let! segments =
            Gen.listOfLength count (Gen.elements [ "src"; "Docs"; "a b"; "été"; "x.env"; "k.pem" ])

        let! separators =
            Gen.listOfLength count (Gen.elements [ "/"; "\\"; "//"; "/./"; "\\.\\" ])

        let! leadingDot = Gen.elements [ true; false ]
        let! trailing = Gen.elements [ ""; "/"; "\\" ]

        let messy =
            (if leadingDot then "./" else "")
            + (List.zip segments separators
               |> List.mapi (fun i (s, sep) -> if i = 0 then s else sep + s)
               |> String.concat "")
            + trailing

        return messy, String.Join("/", segments)
    }

[<Tests>]
let tests =
    testList
        "Exclusions"
        [
            testList
                "paths"
                [
                    test "normalization unifies separators and resolves dots" {
                        for input, expected in
                            [
                                @"src\a\b.txt", "src/a/b.txt"
                                "./src//a/./b/", "src/a/b"
                                "a/../b", "b"
                                "../x/y", "../x/y"
                                "a/../../x", "../x"
                                "/etc/../passwd", "/passwd"
                                "/../x", "/x"
                                @"C:\repo\src\..\x", "C:/repo/x"
                                @"C:\..\x", "C:/x"
                                @"\\server\share\x", "//server/share/x"
                                ".", ""
                                "", ""
                            ] do
                            Expect.equal (WorkspacePath.normalize input) expected input
                    }

                    testPropertyWithConfig config "normalization is idempotent and leaves no \\, . or empty segments"
                    <| Prop.forAll (Arb.fromGen messyPathGen) (fun (messy, clean) ->
                        let normalized = WorkspacePath.normalize messy
                        Expect.equal normalized clean "normalized"
                        Expect.equal (WorkspacePath.normalize normalized) normalized "idempotent")

                    testPropertyWithConfig config "a path excludes the same with / and \\"
                    <| Prop.forAll (Arb.fromGen (Gen.zip patternGen pathGen)) (fun (pattern, path) ->
                        let e = exclusions [ "t", [ pattern ] ]

                        Expect.equal (Exclusions.test e (path.Replace('/', '\\'))) (Exclusions.test e path) "the same")
                ]

            testList
                "globs"
                [
                    test "gitignore semantics, case by case" {
                        for patterns, excluded, kept in table do
                            for path in excluded do
                                Expect.isTrue (excludedBy patterns path) $"{patterns} excludes {path}"

                            for path in kept do
                                Expect.isFalse (excludedBy patterns path) $"{patterns} keeps {path}"
                    }

                    test "patterns that cannot be used are errors, never ignored" {
                        for pattern, reason in
                            [
                                "", "empty"
                                "   ", "empty"
                                "!keep.txt", "negations are not supported"
                                "!", "negations are not supported"
                                "/", "names no path"
                                @"\", "names no path"
                                ".", "names no path"
                                "{a,/}", "names no path"
                                "[z-a]", "reversed"
                                String.replicate 9 "{a,b}", "alternatives"
                            ] do
                            match Glob.parse pattern with
                            | Error message -> Expect.stringContains message reason $"{Json.quote pattern}"
                            | Ok glob -> failtest $"{pattern} parsed as {glob}"
                    }

                    test "comments are not patterns" {
                        for pattern in [ "# secret"; "\t#x" ] do
                            Expect.equal (Glob.parse pattern) (Ok None) pattern
                    }

                    testPropertyWithConfig config "the matcher agrees with a regular-expression model"
                    <| Prop.forAll (Arb.fromGen (Gen.zip patternGen pathGen)) (fun (pattern, path) ->
                        let expected = (oracle pattern).IsMatch path
                        let actual = Glob.isMatch (glob pattern) path false
                        Expect.equal actual expected $"{pattern} on {path}")

                    testPropertyWithConfig config "* and ? never cross a separator"
                    <| Prop.forAll (Arb.fromGen pathGen) (fun path ->
                        let slashes = path |> Seq.filter (fun c -> c = '/') |> Seq.length
                        let stars = String.Join("/", Array.create (slashes + 1) "*")
                        Expect.isTrue (Glob.isMatch (glob ("/" + stars)) path false) "as deep"

                        Expect.isFalse (Glob.isMatch (glob ("/" + stars + "/*")) path false) "deeper"

                        Expect.isFalse (Glob.isMatch (glob "/?") (path + "/x") false) "?")

                    testPropertyWithConfig config "a path excludes itself and everything below it"
                    <| Prop.forAll (Arb.fromGen (Gen.zip pathGen pathGen)) (fun (dir, below) ->
                        Expect.isTrue (excludedBy [ "/" + dir ] dir) "itself"
                        Expect.isTrue (excludedBy [ "/" + dir ] (dir + "/" + below)) "below"
                        Expect.isTrue (excludedBy [ dir + "/**" ] (dir + "/" + below)) "dir/**"
                        Expect.isFalse (excludedBy [ "/" + dir ] ("x" + dir)) "not a longer name")

                    testPropertyWithConfig config "a pattern without a separator matches the name at any depth"
                    <| Prop.forAll (Arb.fromGen (Gen.zip pathGen pathGen)) (fun (dirs, name) ->
                        let name = name.Replace("/", "")
                        Expect.isTrue (excludedBy [ name ] (dirs + "/" + name)) "below"
                        Expect.isTrue (excludedBy [ name ] name) "at the root")
                ]

            testList
                "case"
                [
                    testPropertyWithConfig config "matching ignores the case of the path and of the pattern"
                    <| Prop.forAll (Arb.fromGen (Gen.zip patternGen pathGen)) (fun (pattern, path) ->
                        let e = exclusions [ "t", [ pattern ] ]
                        let upper = exclusions [ "t", [ pattern.ToUpperInvariant() ] ]

                        Expect.equal
                            (Exclusions.excludes upper path)
                            (Exclusions.excludes e path)
                            "an upper-case pattern"

                        Expect.equal
                            (Exclusions.test e (path.ToUpperInvariant()) |> Option.isSome)
                            (Exclusions.test e path |> Option.isSome)
                            "upper")
                ]

            testList
                "lists"
                [
                    test "the first list that excludes a path is named, with its deciding pattern" {
                        let e =
                            exclusions
                                [
                                    "policy", [ "*.pem" ]
                                    "host", [ "secrets/"; "*.pem" ]
                                    "project", [ "*.log" ]
                                ]

                        Expect.equal
                            (Exclusions.test e "a/k.pem")
                            (Some
                                {
                                    Path = "a/k.pem"
                                    Origin = "policy"
                                    Pattern = "*.pem"
                                })
                            "policy first"

                        Expect.equal
                            (Exclusions.test e @"secrets\x.txt"
                             |> Option.map (fun m -> m.Origin, m.Pattern, m.Path))
                            (Some("host", "secrets/", "secrets/x.txt"))
                            "host"

                        Expect.isNone (Exclusions.test e "a.txt") "not excluded"
                        Expect.isSome (Exclusions.testDirectory e "x/secrets") "a directory"
                        Expect.isNone (Exclusions.testDirectory e "x/notes") "another directory"
                    }

                    test "nothing can re-include: negations are errors, in any list" {
                        match
                            Exclusions.create
                                [
                                    {
                                        Origin = "host"
                                        Patterns = [ "*.env" ]
                                    }
                                    {
                                        Origin = "project"
                                        Patterns = [ "!prod.env" ]
                                    }
                                ]
                        with
                        | Error [ issue ] -> Expect.stringContains issue.Message "negations are not supported" "why"
                        | other -> failtest (string other)
                    }

                    testPropertyWithConfig config "adding a list or a pattern only excludes more, in any order"
                    <| Prop.forAll
                        (Arb.fromGen (Gen.zip3 (Gen.listOf patternGen) (Gen.listOf patternGen) pathGen))
                        (fun (first, second, path) ->
                            let one = exclusions [ "a", first ]
                            let two = exclusions [ "a", first; "b", second ]
                            let merged = exclusions [ "a", second @ first ]

                            if Exclusions.excludes one path then
                                Expect.isTrue (Exclusions.excludes two path) "still excluded"
                                Expect.isTrue (Exclusions.excludes merged path) "in one list, in another order"

                            Expect.equal
                                (Exclusions.excludes merged path)
                                (Exclusions.excludes two path)
                                "one list or two")

                    test "invalid patterns are issues at their list and index" {
                        match
                            Exclusions.create
                                [
                                    { Origin = "a"; Patterns = [ "ok" ] }
                                    {
                                        Origin = "b"
                                        Patterns = [ "fine"; "[z-a]" ]
                                    }
                                ]
                        with
                        | Ok _ -> failtest "accepted"
                        | Error [ issue ] -> Expect.equal (JsonPath.render issue.Path) "$[1].patterns[1]" "where"
                        | Error issues -> failtest (string issues)
                    }

                    test "the patterns codec checks every pattern" {
                        let decode json =
                            ExclusionList.patterns.Decode(
                                Json.parse json |> Result.defaultWith (fun e -> failtest (string e))
                            )

                        Expect.isOk (decode """["*.env", "secrets/", "# note"]""") "valid"
                        Expect.isError (decode """["ok", "!neg"]""") "invalid"
                    }

                    test "nothing is excluded without patterns" {
                        Expect.isTrue Exclusions.none.IsEmpty "none"
                        Expect.isNone (Exclusions.test Exclusions.none "a/.env") "none"
                        Expect.isNone (Exclusions.test (exclusions [ "x", [ "*" ] ]) "") "the root"
                    }

                    test "absolute paths match unanchored patterns only" {
                        Expect.isTrue (excludedBy [ "*.env" ] "/home/u/p/.env") "unanchored"
                        Expect.isTrue (excludedBy [ "*.env" ] @"C:\u\p\.env") "Windows"
                        Expect.isFalse (excludedBy [ "p/.env" ] "/home/u/p/.env") "anchored"
                    }
                ]

            testList
                "requests"
                [
                    test "parts quoting excluded files become placeholders; the rest is left alone" {
                        let e = exclusions [ "host", [ "secrets/"; "*.env" ] ]
                        let secret = [ ContentSource.file "secrets/key.txt" ]

                        let media (sources: ContentSource list) : MediaPart =
                            {
                                Kind = MediaKind.Image
                                MediaType = "image/png"
                                Source = MediaSource.Base64 "AAAA"
                                Name = None
                                Sources = sources
                            }

                        let request =
                            { ChatRequest.create
                                  (ModelRef.create "p" "m")
                                  [
                                      Message.user
                                          [
                                              Part.quoting secret "TOKEN=abc"
                                              Part.quoting [ ContentSource.file "README.md" ] "hello"
                                              Part.text "no provenance: TOKEN=abc"
                                              Part.Media(
                                                  media
                                                      [
                                                          {
                                                              Path = @"app\.env"
                                                              Lines = Some { First = 1; Last = 2 }
                                                          }
                                                      ]
                                              )
                                          ]
                                      Message.assistant
                                          [
                                              Part.ToolCall
                                                  {
                                                      Id = "c1"
                                                      Name = "cell"
                                                      Arguments = "{}"
                                                      State = None
                                                  }
                                          ]
                                      Message.user
                                          [
                                              Part.ToolResult
                                                  {
                                                      CallId = "c1"
                                                      Name = "cell"
                                                      Content = [ ToolContent.Text "TOKEN=abc" ]
                                                      IsError = true
                                                      Sources = [ ContentSource.file "SECRETS/other.txt" ]
                                                  }
                                              Part.ToolResult
                                                  {
                                                      CallId = "c2"
                                                      Name = "cell"
                                                      Content =
                                                          [
                                                              ToolContent.Text "an image"
                                                              ToolContent.Media(media secret)
                                                          ]
                                                      IsError = false
                                                      Sources = []
                                                  }
                                          ]
                                  ] with
                                System =
                                    [
                                        Part.quoting [ ContentSource.file "prod.env" ] "SYSTEM SECRET"
                                        Part.text "be nice"
                                    ]
                            }

                        let placeholder (m: ExclusionMatch) = "<excluded " + m.Path + ">"
                        let redacted, found = ExclusionRedaction.redact placeholder e request

                        Expect.equal
                            (found |> List.map (fun f -> f.Location, f.Match.Path))
                            [
                                "system[0]", "prod.env"
                                "messages[0].parts[0]", "secrets/key.txt"
                                "messages[0].parts[3]", "app/.env"
                                "messages[2].parts[0]", "SECRETS/other.txt"
                                "messages[2].parts[1].content[1]", "secrets/key.txt"
                            ]
                            "what was replaced"

                        let expected =
                            { request with
                                System = [ Part.text "<excluded prod.env>"; Part.text "be nice" ]
                                Messages =
                                    [
                                        Message.user
                                            [
                                                Part.text "<excluded secrets/key.txt>"
                                                Part.quoting [ ContentSource.file "README.md" ] "hello"
                                                Part.text "no provenance: TOKEN=abc"
                                                Part.text "<excluded app/.env>"
                                            ]
                                        request.Messages[1]
                                        Message.user
                                            [
                                                Part.ToolResult
                                                    {
                                                        CallId = "c1"
                                                        Name = "cell"
                                                        Content = [ ToolContent.Text "<excluded SECRETS/other.txt>" ]
                                                        IsError = true
                                                        Sources = []
                                                    }
                                                Part.ToolResult
                                                    {
                                                        CallId = "c2"
                                                        Name = "cell"
                                                        Content =
                                                            [
                                                                ToolContent.Text "an image"
                                                                ToolContent.Text "<excluded secrets/key.txt>"
                                                            ]
                                                        IsError = false
                                                        Sources = []
                                                    }
                                            ]
                                    ]
                            }

                        Expect.equal redacted expected "the request"
                        Expect.isEmpty (ExclusionRedaction.find e redacted) "nothing left"
                        Expect.equal (ExclusionRedaction.find e request) found "find agrees with redact"

                        let unchanged, none = ExclusionRedaction.redact placeholder Exclusions.none request
                        Expect.isTrue (obj.ReferenceEquals(unchanged, request)) "no exclusions: the same request"
                        Expect.isEmpty none "nothing found"
                    }

                    test "the placeholder is stable and says nothing of size or digest unless asked for the path" {
                        let m =
                            {
                                Path = "secrets/key.txt"
                                Origin = "host"
                                Pattern = "secrets/"
                            }

                        let plain = Placeholder.text PlaceholderStyle.Plain m

                        Expect.equal
                            plain
                            (Placeholder.text PlaceholderStyle.Plain { m with Path = "other" })
                            "the same for every file"

                        Expect.isFalse (plain.Contains "secrets") "no path"

                        Expect.stringContains
                            (Placeholder.text PlaceholderStyle.WithPath m)
                            "secrets/key.txt"
                            "the path"
                    }

                    test "sources travel in JSON only when there are some, and never to a wire" {
                        let part =
                            Part.quoting
                                [
                                    {
                                        Path = "a.txt"
                                        Lines = Some { First = 2; Last = 5 }
                                    }
                                ]
                                "x"

                        Expect.equal
                            (Json.serialize (Part.codec.Encode part))
                            """{"kind":"text","text":"x","sources":[{"path":"a.txt","lines":{"first":2,"last":5}}]}"""
                            "with sources"

                        Expect.equal (Part.codec.Decode(Part.codec.Encode part)) (Ok part) "round trip"

                        Expect.equal
                            (Json.serialize (Part.codec.Encode(Part.text "x")))
                            """{"kind":"text","text":"x"}"""
                            "without"

                        let request = ChatRequest.create (ModelRef.create "p" "m") [ Message.user [ part ] ]
                        let bare = ChatRequest.withoutSources request
                        Expect.isEmpty (ChatRequest.sources bare) "dropped"
                        Expect.equal bare.Messages[0].Parts [ Part.text "x" ] "the text stays"
                    }

                    test "a line range must not end before it starts" {
                        let decoded =
                            ContentSource.codec.Decode(
                                Json.parse """{"path": "a", "lines": {"first": 3, "last": 2}}"""
                                |> Result.defaultWith (fun e -> failtest (string e))
                            )

                        Expect.isError decoded "reversed"
                    }
                ]
        ]
