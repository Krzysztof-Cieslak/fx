/// The pinned Bun: one exact version, and every release archive for a desktop or server target with
/// its SHA-256. Bumping Bun means replacing this table; README.md says how to take and check it.
module Fx.Ext.RuntimeBun.Pins

open Fx.Sdk

/// Bun 1.4.5, released 2026-11-19.
[<Literal>]
let Version = "1.4.5"

/// What `bun --revision` prints: the version and the commit it was built from. The installer's
/// probe compares it exactly.
[<Literal>]
let Revision = "1.4.5+a7733435c"

/// The release's download directory; `${version}` is the component's version.
[<Literal>]
let ReleaseUrl = "https://github.com/oven-sh/bun/releases/download/bun-v${version}"

/// A release archive: `<Name>.zip`, holding `<Name>/bun` (`bun.exe` on Windows).
type Asset =
    {
        Name: string
        /// The archive's SHA-256, as the release's SHASUMS256.txt lists it.
        Sha256: string
        /// The archive's size in bytes.
        Bytes: int64
        /// The SHA-256 of the executable inside.
        Executable: string
        /// The executable's size in bytes.
        ExecutableBytes: int64
    }

    override this.ToString() = this.Name

let private asset name sha256 bytes executable executableBytes =
    {
        Name = name
        Sha256 = sha256
        Bytes = bytes
        Executable = executable
        ExecutableBytes = executableBytes
    }

/// Every archive of the release for a desktop or server target (not the `-profile`, Android or
/// FreeBSD builds). The digests are the release's SHASUMS256.txt, whose clear-signed copy
/// (SHASUMS256.txt.asc) verifies with Bun's release key ("Robobun <robobun@oven.sh>",
/// F3DC C08A 8572 C074 9B3E 1888 8EAB 4D40 A7B2 2B59). They also match GitHub's asset digests and
/// the downloaded files. The `-baseline` and default x64 archives hold the same executable.
let assets: Asset list =
    [
        asset
            "bun-darwin-aarch64"
            "b5d6bc2e13caf14e57c89661e89efd073ff16165b41bfe43882616410609cd10"
            39_827_261L
            "e2984124f1514e1e6bbda20adedbf07a02c443c6a5828853a15c7c7abf78e19f"
            88_661_024L
        asset
            "bun-darwin-x64"
            "a4972e778cce4b6a194d7d289f326aa61576d71d80b7a53e9da2f3739566e5fd"
            43_473_769L
            "88f97645b270aef20f9f9442f1658461cab3e7128e658af4615144e3396f084a"
            98_251_056L
        asset
            "bun-darwin-x64-baseline"
            "fb0121739a7e80e7511b6bfe4d2116422b48ba18a013acadef0c425348e95891"
            43_473_877L
            "88f97645b270aef20f9f9442f1658461cab3e7128e658af4615144e3396f084a"
            98_251_056L
        asset
            "bun-linux-aarch64"
            "18656a3bf0ea5641f5796ae4213b75255579a70a0fded2f337933785568cdf84"
            51_133_631L
            "04778f1f42399a69d086853e9c7af4acf574b2f21a43d0dc673853d8b84a2923"
            107_405_304L
        asset
            "bun-linux-aarch64-musl"
            "d655765d3a0c0fdf707e27c7aa1a4f2c84f1bbc1f759db7e6e3d3c5be414396c"
            47_901_555L
            "9c48986093073afec8b76a3e285303598dd2462f52748aecb65e4d94866e1ea3"
            99_823_264L
        asset
            "bun-linux-x64"
            "94559b3b528995a49452f9f2404cec74f2efd7d06e0a000a8cd214b02c3666eb"
            51_336_055L
            "f05d28a3862f4b84395c7fa32480b835dd47482e80b7e7a49a115227f0c06083"
            107_394_952L
        asset
            "bun-linux-x64-baseline"
            "dcb23a18a1c2fff12d57bcef696dbbb42eb67fde29f43816ecb595459a24c463"
            51_336_091L
            "f05d28a3862f4b84395c7fa32480b835dd47482e80b7e7a49a115227f0c06083"
            107_394_952L
        asset
            "bun-linux-x64-musl"
            "9b6bdba1fa0914b6adfe48e4438212e2c106d9199b77ba642a9b8a3968a49a59"
            48_273_301L
            "cf5d285ddbe256785a5121a3c113191a6b577504976374c7cfd5aedca609b720"
            100_988_344L
        asset
            "bun-linux-x64-musl-baseline"
            "96fb94108eed872ff623ed39007d6b5699a83a5d0086fcfba84921c18145e3e6"
            48_273_337L
            "cf5d285ddbe256785a5121a3c113191a6b577504976374c7cfd5aedca609b720"
            100_988_344L
        asset
            "bun-windows-aarch64"
            "41f57d49c3c6811888d9633c569e40b4eb1f7e92b5e5336e3abd3af03187d77a"
            49_051_344L
            "8bd7f9210f9d58cc21f398d1478e53b2ff919a0c8fc935b5086f3f64667cec6d"
            100_287_576L
        asset
            "bun-windows-x64"
            "815ab45ec7f549abf87e6869584b506eab088e24225683957b7e150b47d6de30"
            54_437_990L
            "e06ad0864463c68892ae71a2a64af7eae1585cac5dd9b80bc042f45605587cb1"
            115_378_776L
        asset
            "bun-windows-x64-baseline"
            "3c081c73865e7a3453a9d514cc2ba3d699880621bcf67bb17241ff5194cb1d4b"
            54_438_042L
            "e06ad0864463c68892ae71a2a64af7eae1585cac5dd9b80bc042f45605587cb1"
            115_378_776L
    ]

/// The archive a target installs. On x64 it is the `-baseline` build (for CPUs without AVX2): in
/// 1.4.5 it holds the very executable the default build does (`Executable` is equal), so it costs
/// nothing and also runs where AVX2 is missing or masked (old servers, some VMs and emulators).
/// Targets have no CPU-feature dimension for that reason. A bump must check the pair again
/// (README.md).
let assetFor (target: TargetTriple) : string =
    match target.Os, target.Arch, target.Libc with
    | Os.Linux, Arch.X64, Some Libc.Musl -> "bun-linux-x64-musl-baseline"
    | Os.Linux, Arch.X64, _ -> "bun-linux-x64-baseline"
    | Os.Linux, Arch.Arm64, Some Libc.Musl -> "bun-linux-aarch64-musl"
    | Os.Linux, Arch.Arm64, _ -> "bun-linux-aarch64"
    | Os.MacOs, Arch.X64, _ -> "bun-darwin-x64-baseline"
    | Os.MacOs, Arch.Arm64, _ -> "bun-darwin-aarch64"
    | Os.Windows, Arch.X64, _ -> "bun-windows-x64-baseline"
    | Os.Windows, Arch.Arm64, _ -> "bun-windows-aarch64"

/// The pinned asset of a name.
let find (name: string) : Asset =
    match assets |> List.tryFind (fun a -> a.Name = name) with
    | Some asset -> asset
    | None -> invalidArg (nameof name) $"no pinned Bun archive is named {name}"
