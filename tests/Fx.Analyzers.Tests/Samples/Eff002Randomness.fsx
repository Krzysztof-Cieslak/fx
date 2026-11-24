// EFF002: randomness and id generation outside Eff; ids come from `IIds`.
open System
open System.Security.Cryptography

let id = Guid.NewGuid() // expect: EFF002
let ordered = Guid.CreateVersion7() // expect: EFF002
let shared = Random.Shared.Next() // expect: EFF002
let seeded = Random(42) // expect: EFF002
let bytes = RandomNumberGenerator.GetBytes 16 // expect: EFF002
let fileName = IO.Path.GetRandomFileName() // expect: EFF002
