// EFF004: filesystem access outside Eff; files are reached through `IFs`.
open System.IO

let text = File.ReadAllText "notes.txt" // expect: EFF004
let files = Directory.GetFiles "." // expect: EFF004
let size = FileInfo("notes.txt").Length // expect: EFF004
let stream = new FileStream("notes.txt", FileMode.Open) // expect: EFF004
let reader = new StreamReader("notes.txt") // expect: EFF004
let writer = new StreamWriter("notes.txt") // expect: EFF004
let temp = Path.GetTempPath() // expect: EFF004
let full = Path.GetFullPath "notes.txt" // expect: EFF004
let exists = Path.Exists "notes.txt" // expect: EFF004
