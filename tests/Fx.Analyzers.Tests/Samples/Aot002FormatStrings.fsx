// AOT002: printf format specifiers and composite format strings; use plain interpolation instead.
let count = 3
let name = "x"
let ratio = 0.5

let decimal = sprintf "%d items" count // expect: AOT002
let text = sprintf "name: %s" name // expect: AOT002
let fixedPoint = sprintf "%.2f" ratio // expect: AOT002
let boxed = sprintf "%O" (box count) // expect: AOT002
let failing () : unit = failwithf "missing %s" name // expect: AOT002
let viaKprintf = Printf.kprintf id "%i" count // expect: AOT002
let interpolatedDecimal = $"%d{count} items" // expect: AOT002
let interpolatedText = $"name: %s{name}" // expect: AOT002
let interpolatedFloat = $"%.2f{ratio}" // expect: AOT002
let printfInterpolated () : unit = failwithf $"missing {name}" // expect: AOT002
let composite = System.String.Format("{0} items", count) // expect: AOT002
let builder = System.Text.StringBuilder().AppendFormat("{0}", count) // expect: AOT002
let writeTo (writer: System.IO.TextWriter) = writer.WriteLine("{0} items", count) // expect: AOT002
