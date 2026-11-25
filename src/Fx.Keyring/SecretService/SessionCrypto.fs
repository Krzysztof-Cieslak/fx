/// The Secret Service's `dh-ietf1024-sha256-aes128-cbc-pkcs7` session algorithm
/// (https://specifications.freedesktop.org/secret-service-spec/latest/transfer-secrets.html): a
/// Diffie-Hellman exchange in the 1024-bit MODP group of RFC 2409 (generator 2), HKDF-SHA256 with no
/// salt and no info to a 128-bit AES key, and AES-128-CBC with PKCS#7 padding and a fresh IV per
/// secret. Pure: the caller supplies the random bytes.
///
/// It keeps secrets off the bus in clear text; anything that can read this process's memory can read
/// them anyway, so it is defence in depth against bus monitors, not a boundary.
module Fx.Keyring.SecretService.SessionCrypto

open System
open System.Numerics
open System.Security.Cryptography

/// The algorithm's name in `OpenSession`.
[<Literal>]
let Algorithm = "dh-ietf1024-sha256-aes128-cbc-pkcs7"

/// RFC 2409 section 6.2, the Second Oakley Group: 2^1024 - 2^960 - 1 + 2^64 * (floor(2^894 pi) + 129093).
let private primeHex =
    "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DD"
    + "EF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED"
    + "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE65381FFFFFFFFFFFFFFFF"

let prime =
    BigInteger(ReadOnlySpan(Convert.FromHexString primeHex), isUnsigned = true, isBigEndian = true)

let private generator = BigInteger 2

/// The length of the group's numbers in bytes.
[<Literal>]
let KeyBytes = 128

let private toBytes (n: BigInteger) =
    n.ToByteArray(isUnsigned = true, isBigEndian = true)

let private ofBytes (bytes: byte array) =
    BigInteger(ReadOnlySpan bytes, isUnsigned = true, isBigEndian = true)

/// One side of the exchange.
[<Sealed>]
type KeyPair private (privateKey: BigInteger) =
    /// The public key, big-endian, as sent in `OpenSession`.
    member _.Public = toBytes (BigInteger.ModPow(generator, privateKey, prime))

    member internal _.Private = privateKey

    override _.ToString() = "KeyPair(redacted)"

    /// A key pair from 128 random bytes (the private exponent). `None` for the negligible share of
    /// values outside 2..p-2, so the caller draws again.
    static member FromRandom(random: byte array) : KeyPair option =
        if random.Length <> KeyBytes then
            invalidArg (nameof random) "128 random bytes"

        let x = ofBytes random

        if x < BigInteger 2 || x > prime - BigInteger 2 then
            None
        else
            Some(KeyPair x)

/// The AES key both sides derive from the exchange, or an error if the peer's public key is not a
/// valid group element (it must be in 2..p-2, which rules out the degenerate values 0, 1 and p-1).
let deriveKey (pair: KeyPair) (peerPublic: byte array) : Result<byte array, string> =
    let y = ofBytes peerPublic

    if peerPublic.Length > KeyBytes || y < BigInteger 2 || y > prime - BigInteger 2 then
        Error "the service's public key is not a valid group element"
    else
        let shared = toBytes (BigInteger.ModPow(y, pair.Private, prime))
        // Left-padded to the prime's length, as libsecret and gnome-keyring do.
        let padded = Array.zeroCreate<byte> KeyBytes
        shared.CopyTo(padded, KeyBytes - shared.Length)
        Ok(HKDF.DeriveKey(HashAlgorithmName.SHA256, padded, 16, Array.empty, Array.empty))

/// Encrypts a secret with a fresh 16-byte IV: (parameters = IV, value = ciphertext).
let encrypt (key: byte array) (iv: byte array) (plaintext: byte array) : byte array * byte array =
    if iv.Length <> 16 then
        invalidArg (nameof iv) "16 bytes"

    use aes = Aes.Create()
    aes.Key <- key
    iv, aes.EncryptCbc(plaintext, iv, PaddingMode.PKCS7)

/// Decrypts a secret: `Error` if the parameters are not an IV or the padding is wrong.
let decrypt (key: byte array) (parameters: byte array) (ciphertext: byte array) : Result<byte array, string> =
    if parameters.Length <> 16 then
        Error "the secret's parameters are not a 16-byte IV"
    elif ciphertext.Length = 0 || ciphertext.Length % 16 <> 0 then
        Error "the secret's value is not whole AES blocks"
    else
        use aes = Aes.Create()
        aes.Key <- key

        try
            Ok(aes.DecryptCbc(ciphertext, parameters, PaddingMode.PKCS7))
        with :? CryptographicException ->
            Error "the secret does not decrypt with the session key"
