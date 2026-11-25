module Fx.Grants.Tests.PatternTests

open Expecto
open FsCheck
open FsCheck.FSharp
open Fx.Grants
open Fx.Grants.Tests.Support

let private matches kind pattern resource = Pattern.matches kind pattern resource

let private kinds =
    Gen.elements [ ResourceKind.Path; ResourceKind.Command; ResourceKind.Name ]

[<Tests>]
let tests =
    testList
        "Patterns"
        [
            test "a path's `*` stays in its segment, `**` spans segments" {
                let path = ResourceKind.Path
                Expect.isTrue (matches path "src/*" "src/a.fs") "one level"
                Expect.isFalse (matches path "src/*" "src/a/b.fs") "not deeper"
                Expect.isTrue (matches path "src/**" "src/a/b.fs") "any depth"
                Expect.isTrue (matches path "src/**" "src") "`**` matches no segment too"
                Expect.isTrue (matches path "**/*.fs" "a.fs") "leading `**`"
                Expect.isTrue (matches path "**/*.fs" "x/y/a.fs") "leading `**`, deep"
                Expect.isTrue (matches path "src/**/test/*.fs" "src/a/b/test/x.fs") "middle `**`"
                Expect.isFalse (matches path "src/**/test/*.fs" "src/a/b/x.fs") "middle `**` needs the rest"
                Expect.isTrue (matches path "/etc/*" "/etc/hosts") "absolute"
                Expect.isFalse (matches path "/etc/*" "etc/hosts") "absolute is not relative"
                Expect.isTrue (matches path "a?.fs" "ab.fs") "`?` is one character"
                Expect.isFalse (matches path "a?.fs" "a.fs") "`?` is not zero characters"
                Expect.isFalse (matches path "a?b" "a/b") "`?` is not a separator"
            }

            test "a command's `*` is one word, `**` any words" {
                let command = ResourceKind.Command
                Expect.isTrue (matches command "git status" "git status") "exact"
                Expect.isTrue (matches command "git *" "git status") "one word"
                Expect.isFalse (matches command "git *" "git log -n 3") "not several words"
                Expect.isTrue (matches command "git **" "git log -n 3") "any words"
                Expect.isTrue (matches command "git **" "git") "no words"
                Expect.isTrue (matches command "npm run test*" "npm run test:unit") "a prefix of a word"
                Expect.isFalse (matches command "git push" "git push --force") "exact means exact"
            }

            test "a name's `*` matches anything" {
                let name = ResourceKind.Name
                Expect.isTrue (matches name "*.github.com:443" "api.github.com:443") "a subdomain"
                Expect.isTrue (matches name "*.github.com:*" "a.b.github.com:22") "nested, any port"
                Expect.isFalse (matches name "*.github.com:443" "evilgithub.com:443") "a suffix, not a substring"
                Expect.isTrue (matches name "copilot/*" "copilot/gpt-5") "`/` is a plain character"
            }

            test "escapes match the character itself" {
                Expect.isTrue (matches ResourceKind.Name "a\\*b" "a*b") "escaped star"
                Expect.isFalse (matches ResourceKind.Name "a\\*b" "axb") "escaped star is not a wildcard"
                Expect.equal (Pattern.exact "a*b?c\\d") "a\\*b\\?c\\\\d" "exact escapes"
            }

            test "case: exact for allows, ignored for denies" {
                Expect.isFalse (matches ResourceKind.Path "src/**" "Src/a.fs") "exact"
                Expect.isTrue (Pattern.matchesIgnoringCase ResourceKind.Path "src/**" "Src/a.fs") "ignoring case"
            }

            test "invalid patterns say why and match nothing" {
                let invalid kind pattern =
                    match Pattern.validate kind pattern with
                    | Ok() -> failtest ("accepted " + pattern)
                    | Error reason -> reason

                Expect.stringContains (invalid ResourceKind.Path "") "empty" "empty"
                Expect.stringContains (invalid ResourceKind.Path "src**") "whole segment" "`**` inside a segment"
                Expect.stringContains (invalid ResourceKind.Path "a/***") "whole segment" "three stars"
                Expect.stringContains (invalid ResourceKind.Path "a//b") "empty segment" "`//`"
                Expect.stringContains (invalid ResourceKind.Path "a/") "empty segment" "trailing `/`"
                Expect.stringContains (invalid ResourceKind.Path "a/../b") "`..`" "`..`"
                Expect.stringContains (invalid ResourceKind.Command "git  log") "single spaces" "double space"
                Expect.stringContains (invalid ResourceKind.Command " git") "single spaces" "leading space"
                Expect.stringContains (invalid ResourceKind.Name "a**") "in a name" "`**` in a name"
                Expect.stringContains (invalid ResourceKind.Name "a\\") "end with" "trailing escape"
                Expect.stringContains (invalid ResourceKind.Name "a\nb") "control" "a newline"

                Expect.stringContains (invalid ResourceKind.Path "**/a/**/b/**/c/**/d/**") "at most" "too many `**`"

                Expect.isFalse (matches ResourceKind.Path "a//b" "a//b") "an invalid pattern matches nothing"
            }

            test "resources must be canonical" {
                let invalid kind resource =
                    match Resource.validate kind resource with
                    | Ok() -> failtest ("accepted " + resource)
                    | Error reason -> reason

                Expect.stringContains (invalid ResourceKind.Path "src/../secrets") "canonical" "`..`"
                Expect.stringContains (invalid ResourceKind.Path "./a") "canonical" "`.`"
                Expect.stringContains (invalid ResourceKind.Path "a\\b") "`\\`" "backslash"
                Expect.stringContains (invalid ResourceKind.Path "a/") "empty segment" "trailing `/`"
                Expect.stringContains (invalid ResourceKind.Command "git status\nrm -rf /") "control" "a newline"
                Expect.stringContains (invalid ResourceKind.Command "git  status") "single spaces" "double space"
                Expect.equal (Resource.validate ResourceKind.Path "/etc/hosts") (Ok()) "absolute"
                Expect.equal (Resource.validate ResourceKind.Path "C:/Users/me") (Ok()) "a drive"
                Expect.isTrue (Resource.isRepoPath "") "the root"
                Expect.isFalse (Resource.isRepoPath "/src") "absolute is not repo-relative"
                Expect.isFalse (Resource.isRepoPath "C:/src") "a drive is not repo-relative"
            }

            test "specificity: exact, then literals, then fewer `**` and `*`, then more `?`" {
                let s = Pattern.specificity ResourceKind.Path

                let more a b =
                    Pattern.compareSpecificity (s a) (s b) > 0

                Expect.isTrue (more "src/a.fs" "src/*.fs") "exact first"
                Expect.isTrue (more "src/*.fs" "src/*") "more literals"
                Expect.isTrue (more "src/*" "src/**") "`*` before `**`"
                Expect.isTrue (more "a?" "a*") "`?` before `*`"
                Expect.isTrue (more "*?" "*") "`?` constrains"
                Expect.equal (Pattern.compareSpecificity (s "a/*") (s "b/*")) 0 "equal"
            }

            testPropertyWithConfig config "a resource's exact pattern matches it, and is the most specific that does"
            <| Prop.forAll
                (Arb.fromGen (
                    gen {
                        let! kind = kinds
                        let! pattern, r = generalized kind
                        return kind, pattern, r
                    }
                ))
                (fun (kind, pattern, r) ->
                    let exact = Pattern.exact r

                    Pattern.matches kind exact r
                    && (pattern = exact
                        || Pattern.compareSpecificity
                            (Pattern.specificity kind exact)
                            (Pattern.specificity kind pattern)
                            >
                            0))

            testPropertyWithConfig config "generalizing a pattern keeps it matching and makes it less specific"
            <| Prop.forAll
                (Arb.fromGen (
                    gen {
                        let! kind = kinds
                        let! pattern, r = generalized kind
                        let! choice = Gen.choose (0, 1000)
                        return kind, pattern, r, choice
                    }
                ))
                (fun (kind, _, r, choice) ->
                    // Generalize step by step from the exact pattern, checking each step.
                    let rec walk (tokens: PatternToken list) (steps: int) =
                        if steps = 0 then
                            true
                        else
                            match generalize kind (choice + steps) tokens with
                            | None -> true
                            | Some next ->
                                let before = render kind tokens
                                let after = render kind next

                                Pattern.validate kind after = Ok()
                                && Pattern.matches kind after r
                                && Pattern.compareSpecificity
                                    (Pattern.specificity kind before)
                                    (Pattern.specificity kind after)
                                    >
                                    0
                                && walk next (steps - 1)

                    walk (tokensOf kind r) 8)

            testPropertyWithConfig config "what matches exactly matches ignoring case"
            <| Prop.forAll
                (Arb.fromGen (
                    gen {
                        let! kind = kinds
                        let! pattern, _ = generalized kind
                        let! r = resource kind
                        return kind, pattern, r
                    }
                ))
                (fun (kind, pattern, r) ->
                    not (Pattern.matches kind pattern r)
                    || Pattern.matchesIgnoringCase kind pattern r)

            testPropertyWithConfig config "a path pattern ending in `/**` matches everything under its prefix"
            <| Prop.forAll
                (Arb.fromGen (Gen.zip (resource ResourceKind.Path) (resource ResourceKind.Path)))
                (fun (prefix, rest) ->
                    let rest = rest.TrimStart '/'
                    let pattern = Pattern.exact prefix + "/**"

                    Pattern.matches ResourceKind.Path pattern prefix
                    && Pattern.matches ResourceKind.Path pattern (prefix + "/" + rest))
        ]
