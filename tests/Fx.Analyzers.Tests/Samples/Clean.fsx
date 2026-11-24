// Constructs the analyzers must accept: nothing in this file may be reported.
open System
open System.IO
open System.Text

type Shape =
    | Circle of radius: float
    | Square of side: float

    override s.ToString() =
        match s with
        | Circle r -> $"circle {r}"
        | Square a -> $"square {a}"

let count = 3
let ratio = 0.25
let shape = Circle 1.0

let interpolated = $"{count} items of {shape}"
let dotnetFormat = $"{ratio:P1} at {count, 5}"
let percent = $"100%% done"
let verbatim = $@"C:\{count}"
let plainPrintf = sprintf "no specifiers"
let escaped = sprintf "100%%"
let failure () : unit = failwith $"missing {count}"
let name = nameof count
let typeName = typeof<Shape>.Name
let fullName = shape.GetType().FullName
let genericName = typedefof<list<int>>.Name
let padded = count.ToString "D3"
let joined = String.concat ", " [ "a"; "b" ]
let built = StringBuilder().Append(count).ToString()
let path = Path.Combine("a", "b")
let fileName = Path.GetFileName "a/b.txt"
let resolved = Path.GetFullPath("b.txt", "/a")
let newline = Environment.NewLine
let windows = OperatingSystem.IsWindows()
let date = DateTime(2026, 1, 1)
let timeout = TimeSpan.FromSeconds 5.0
let parsed = Guid.Parse "00000000-0000-0000-0000-000000000000"
let readAll (stream: Stream) = (new StreamReader(stream)).ReadToEnd()
let write (writer: TextWriter) = writer.WriteLine $"{count}"
let startInfo = Diagnostics.ProcessStartInfo("git", "status")

let request =
    new Net.Http.HttpRequestMessage(Net.Http.HttpMethod.Get, "https://example.com")

let computed = async { return count }
let started = task { return count }
let days = Enum.GetValues<DayOfWeek>()
