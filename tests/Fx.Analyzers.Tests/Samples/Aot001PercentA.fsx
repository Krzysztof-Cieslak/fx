// AOT001: `%A` formats values with runtime reflection.
// Every finding is marked with `// expect: <codes>` on its line; unmarked lines must stay clean.
let point = (1, 2)

let viaSprintf = sprintf "%A" point // expect: AOT001
let padded = sprintf "%-10A" point // expect: AOT001
let interpolated = $"point: %A{point}" // expect: AOT001
let failing () : unit = failwithf "bad %A" point // expect: AOT001
let viaKsprintf = Printf.ksprintf id "%A" point // expect: AOT001
let mixed = sprintf "%d of %A" 1 point // expect: AOT001 AOT002
