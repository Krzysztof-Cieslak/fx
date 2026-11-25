/// The seccomp program, run through a BPF interpreter as the kernel would run it.
module Fx.Ext.SandboxBubblewrap.Tests.SeccompTests

open Expecto
open Fx.Ext.SandboxBubblewrap
open Fx.Ext.SandboxBubblewrap.Seccomp
open Fx.Sdk

let private run (arch: Arch) (name: string) (args: uint32 list) =
    match numberOf arch name with
    | Some nr -> evaluate (instructions arch) (auditArch arch) nr args
    | None -> failtest $"{name} has no number on {arch}"

let private allowed (arch: Arch) (nr: uint32) =
    evaluate (instructions arch) (auditArch arch) nr []

[<Tests>]
let tests =
    testList
        "seccomp"
        [
            for arch in [ Arch.X64; Arch.Arm64 ] do
                test $"{arch}: ordinary calls are allowed" {
                    // read, write, openat, execve, exit_group, getpid
                    let ordinary =
                        match arch with
                        | Arch.X64 -> [ 0u; 1u; 257u; 59u; 231u; 39u ]
                        | Arch.Arm64 -> [ 63u; 64u; 56u; 221u; 94u; 172u ]

                    for nr in ordinary do
                        Expect.equal (allowed arch nr) Action.Allow $"call {nr}"
                }

                test $"{arch}: what reaches outside the sandbox is refused with EPERM" {
                    for call in refused do
                        match numberOf arch call.Name with
                        | Some _ -> Expect.equal (run arch call.Name []) (Action.Errno EPERM) call.Name
                        | None -> ()
                }

                test $"{arch}: clone3 is ENOSYS, so libc falls back to clone" {
                    Expect.equal (run arch "clone3" []) (Action.Errno ENOSYS) "clone3"
                }

                test $"{arch}: no new user namespaces, other namespaces' flags pass to the kernel" {
                    Expect.equal (run arch "unshare" [ CloneNewUser ]) (Action.Errno EPERM) "unshare(CLONE_NEWUSER)"
                    Expect.equal (run arch "unshare" [ CloneNewUser ||| 0x20000u ]) (Action.Errno EPERM) "with others"
                    Expect.equal (run arch "unshare" [ 0x20000u ]) Action.Allow "unshare(CLONE_NEWNS)"

                    Expect.equal
                        (run arch "clone" [ CloneNewUser ||| 0x11u ])
                        (Action.Errno EPERM)
                        "clone(CLONE_NEWUSER)"

                    Expect.equal (run arch "clone" [ 0x3d0f00u ]) Action.Allow "a thread"
                }

                test $"{arch}: no typing into the terminal" {
                    Expect.equal (run arch "ioctl" [ 0u; 0x5412u ]) (Action.Errno EPERM) "TIOCSTI"
                    Expect.equal (run arch "ioctl" [ 0u; 0x541Cu ]) (Action.Errno EPERM) "TIOCLINUX"
                    Expect.equal (run arch "ioctl" [ 0u; 0x5401u ]) Action.Allow "TCGETS"
                    Expect.equal (run arch "ioctl" [ 0u; 0x5413u ]) Action.Allow "TIOCGWINSZ"
                }

                test $"{arch}: another architecture's calls kill the process" {
                    Expect.equal (evaluate (instructions arch) 0x40000003u 1u []) Action.Kill "i386"
                }

                test $"{arch}: bwrap reads it as sock_filter entries" {
                    let bytes = program arch
                    Expect.equal bytes.Length (8 * (instructions arch).Length) "8 bytes each"
                    Expect.isLessThan (instructions arch).Length 4096 "within BPF_MAXINSNS"
                    // The first instruction loads the architecture: BPF_LD|BPF_W|BPF_ABS, offset 4.
                    Expect.equal bytes[0..7] [| 0x20uy; 0uy; 0uy; 0uy; 4uy; 0uy; 0uy; 0uy |] "ld arch"
                }

            test "x86-64: x32 calls kill the process" {
                Expect.equal (allowed Arch.X64 (X32Bit ||| 1u)) Action.Kill "x32 write"
            }
        ]
