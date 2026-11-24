/// Compares a JIT run with a native run of the same case. Pure.
module Fx.Aot.Diff.Diff

open System.Text

/// What one process run produced.
type Run =
    {
        ExitCode: int
        Stdout: string
        Stderr: string
    }

/// One line of a line diff.
type Line =
    | Same of string
    | Removed of string
    | Added of string

    override this.ToString() =
        match this with
        | Same l -> "  " + l
        | Removed l -> "- " + l
        | Added l -> "+ " + l

/// Makes control characters visible, e.g. a `\r` before the newline on one side only.
let visible (line: string) : string =
    let sb = StringBuilder()

    for c in line do
        match c with
        | '\r' -> sb.Append "\\r" |> ignore
        | '\t' -> sb.Append "\\t" |> ignore
        | c when c < ' ' -> sb.Append("\\u").Append((int c).ToString("x4")) |> ignore
        | c -> sb.Append c |> ignore

    sb.ToString()

/// Splits text into visible lines. A missing final newline is marked, so that it shows in a diff.
let lines (text: string) : string list =
    if text.Length = 0 then
        []
    else
        let parts = text.Split '\n' |> Array.map visible |> List.ofArray

        if text.EndsWith '\n' then
            List.take (parts.Length - 1) parts
        else
            List.updateAt (parts.Length - 1) (List.last parts + " (no newline at end)") parts

/// A line diff of `before` and `after`, from their longest common subsequence.
let diffLines (before: string list) (after: string list) : Line list =
    let a = Array.ofList before
    let b = Array.ofList after
    // lcs[i, j] = length of the longest common subsequence of a[i..] and b[j..].
    let lcs = Array2D.zeroCreate (a.Length + 1) (b.Length + 1)

    for i in a.Length - 1 .. -1 .. 0 do
        for j in b.Length - 1 .. -1 .. 0 do
            lcs[i, j] <-
                if a[i] = b[j] then
                    lcs[i + 1, j + 1] + 1
                else
                    max lcs[i + 1, j] lcs[i, j + 1]

    let rec walk i j acc =
        if i < a.Length && j < b.Length && a[i] = b[j] then
            walk (i + 1) (j + 1) (Same a[i] :: acc)
        elif i < a.Length && (j = b.Length || lcs[i + 1, j] >= lcs[i, j + 1]) then
            walk (i + 1) j (Removed a[i] :: acc)
        elif j < b.Length then
            walk i (j + 1) (Added b[j] :: acc)
        else
            List.rev acc

    walk 0 0 []

/// Renders a diff, keeping `context` unchanged lines around each change and eliding the rest.
let render (context: int) (diff: Line list) : string list =
    let lines = Array.ofList diff

    let changed i =
        match lines[i] with
        | Same _ -> false
        | Removed _
        | Added _ -> true

    let near i =
        seq { max 0 (i - context) .. min (lines.Length - 1) (i + context) }
        |> Seq.exists changed

    let output = ResizeArray<string>()
    let mutable elided = false

    for i in 0 .. lines.Length - 1 do
        if near i then
            elided <- false
            output.Add(string lines[i])
        elif not elided then
            elided <- true
            output.Add "  ..."

    List.ofSeq output

/// How a native run differs from the JIT run of the same case, as report lines. Empty when identical.
let compare (jit: Run) (native: Run) : string list =
    let stream name (j: string) (n: string) =
        if j = n then
            []
        else
            $"{name} (- jit, + native):" :: render 3 (diffLines (lines j) (lines n))

    [
        if jit.ExitCode <> native.ExitCode then
            yield $"exit code: jit {jit.ExitCode}, native {native.ExitCode}"
        yield! stream "stdout" jit.Stdout native.Stdout
        yield! stream "stderr" jit.Stderr native.Stderr
    ]
