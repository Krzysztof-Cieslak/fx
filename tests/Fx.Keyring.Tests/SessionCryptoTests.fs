module Fx.Keyring.Tests.SessionCryptoTests

open System
open System.Text
open Expecto
open Fx.Keyring.SecretService

let private pair (privateKey: byte array) =
    match SessionCrypto.KeyPair.FromRandom privateKey with
    | Some pair -> pair
    | None -> failtest "private key out of range"

let private clientPrivate = Array.init 128 (fun i -> byte (i + 1))
let private servicePrivate = Array.init 128 (fun i -> byte ((7 * i + 3) % 256))

let private ok (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error e -> failtest e

/// Known answers computed independently: the key with Python (pow, hmac and hashlib, the way the
/// SecretStorage library does it), the ciphertext with `openssl enc -aes-128-cbc`.
module Known =
    let clientPublic =
        "52893c96a07492593d36d00328ba21101c0ab104717cf1928b3890e1ecbd76e827e7ba7d589a03ef64f8f5648cea2b07"
        + "7409319013d832c41a04a3793eb4b73ca2a36306241de180ec36f5cfbef207ba2e04a39f1384dce12b8ebe27b54dbf04"
        + "9be0abc80ebd4849976974b2be955c2c87ba11712576887f5b96427a1d8fb2fd"

    let servicePublic =
        "7b1d7d3188d79522e49e1ef8a54a32403fa559965ff46561a8d12fc1f79273e70b2c6ea601992ec15910b87dffa61090"
        + "4bdbc2bdd35b13c95a0d7cb40a0d62720f4a904259982b507d9981f4eedb7371db1336d26e29d58e2d2da00d7928a64c"
        + "adc4eb683c04df21ac4f5f71174805326a640a789679de2fc1b3ed28d2c9ba71"

    let key = "d3f57bcf025af223fe1dcb0b005ab0b1"
    let iv = "000102030405060708090a0b0c0d0e0f"
    let plaintext = "correct horse battery staple"
    let ciphertext = "9868e9a6a715976e660247c358fad46e2bc5872ae958f6aafa5b8f8da9f5be05"

let private hex (bytes: byte array) = Convert.ToHexStringLower bytes

[<Tests>]
let tests =
    testList
        "Secret Service session crypto"
        [
            test "both sides derive the known key" {
                let client = pair clientPrivate
                let service = pair servicePrivate
                Expect.equal (hex client.Public) Known.clientPublic "client public key"
                Expect.equal (hex service.Public) Known.servicePublic "service public key"
                Expect.equal (hex (SessionCrypto.deriveKey client service.Public |> ok)) Known.key "client side"
                Expect.equal (hex (SessionCrypto.deriveKey service client.Public |> ok)) Known.key "service side"
            }

            test "secrets encrypt to the known ciphertext and decrypt back" {
                let key = Convert.FromHexString Known.key
                let iv = Convert.FromHexString Known.iv

                let parameters, value =
                    SessionCrypto.encrypt key iv (Encoding.UTF8.GetBytes Known.plaintext)

                Expect.equal (hex parameters) Known.iv "parameters are the IV"
                Expect.equal (hex value) Known.ciphertext "ciphertext"

                Expect.equal
                    (SessionCrypto.decrypt key parameters value |> ok |> Encoding.UTF8.GetString)
                    Known.plaintext
                    "round trip"
            }

            test "degenerate public keys, wrong keys and broken parameters are errors" {
                let client = pair clientPrivate
                let prime = SessionCrypto.prime.ToByteArray(isUnsigned = true, isBigEndian = true)
                let primeMinusOne = Array.copy prime
                primeMinusOne[127] <- primeMinusOne[127] - 1uy

                for peer in [ [| 0uy |]; [| 1uy |]; primeMinusOne; prime; Array.create 129 1uy ] do
                    Expect.isError (SessionCrypto.deriveKey client peer) $"peer {hex peer}"

                let key = Convert.FromHexString Known.key
                let ciphertext = Convert.FromHexString Known.ciphertext
                let iv = Convert.FromHexString Known.iv
                Expect.isError (SessionCrypto.decrypt key [| 1uy |] ciphertext) "short IV"
                Expect.isError (SessionCrypto.decrypt key iv ciphertext[..14]) "partial block"
                Expect.isError (SessionCrypto.decrypt (Array.zeroCreate 16) iv ciphertext) "wrong key"
            }

            test "private keys outside 2..p-2 are drawn again" {
                Expect.isNone (SessionCrypto.KeyPair.FromRandom(Array.zeroCreate 128)) "zero"
                Expect.isNone (SessionCrypto.KeyPair.FromRandom(Array.create 128 0xffuy)) "above p"

                Expect.throwsT<ArgumentException>
                    (fun () -> SessionCrypto.KeyPair.FromRandom [| 1uy |] |> ignore)
                    "length"
            }
        ]
