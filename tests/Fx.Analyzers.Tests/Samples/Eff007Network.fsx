// EFF007: network and inter-process communication outside Eff.
open System.Net

let client = new Http.HttpClient() // expect: EFF007
let host = Dns.GetHostName() // expect: EFF007
let tcp = new Sockets.TcpClient() // expect: EFF007
let socket = new WebSockets.ClientWebSocket() // expect: EFF007
let pipe = new System.IO.Pipes.NamedPipeClientStream("x") // expect: EFF007
