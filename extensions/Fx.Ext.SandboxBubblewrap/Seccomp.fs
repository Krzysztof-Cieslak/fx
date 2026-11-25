/// The seccomp filter `fx.sandbox.bubblewrap` gives bwrap (`--seccomp <fd>`): a classic BPF program,
/// built here as data for each architecture, that refuses the system calls a sandboxed coding agent
/// has no use for and that widen the kernel's attack surface or reach outside the sandbox. bwrap
/// installs it just before it runs the command, after it set up the namespaces. See README.md.
module Fx.Ext.SandboxBubblewrap.Seccomp

open System
open Fx.Sdk

/// What the filter does with a system call.
[<RequireQualifiedAccess>]
type Action =
    | Allow
    /// Fails with this errno.
    | Errno of int
    /// Kills the process (a system call of another architecture or ABI).
    | Kill

    override this.ToString() =
        match this with
        | Allow -> "allow"
        | Errno errno -> $"errno {errno}"
        | Kill -> "kill"

/// One classic BPF instruction (`struct sock_filter`).
type Instruction =
    {
        Code: uint16
        Jt: byte
        Jf: byte
        K: uint32
    }

    override this.ToString() =
        $"{this.Code:x4} {this.Jt} {this.Jf} {this.K:x8}"

/// A system call to filter, by name, with its number on each architecture (`None`: it has none).
type Syscall =
    {
        Name: string
        X64: int option
        Arm64: int option
    }

    override this.ToString() = this.Name

[<Literal>]
let EPERM = 1

[<Literal>]
let ENOSYS = 38

/// `AUDIT_ARCH_X86_64` and `AUDIT_ARCH_AARCH64`.
let auditArch (arch: Arch) : uint32 =
    match arch with
    | Arch.X64 -> 0xC000003Eu
    | Arch.Arm64 -> 0xC00000B7u

/// The x32 ABI's bit in x86-64 system call numbers.
[<Literal>]
let X32Bit = 0x40000000u

/// `CLONE_NEWUSER`: a new user namespace (bwrap's `--disable-userns` stops it too, where bwrap has it).
[<Literal>]
let CloneNewUser = 0x10000000u

/// `TIOCSTI` (types into a terminal) and `TIOCLINUX` (the Linux console's selection).
let refusedIoctls: uint32 list = [ 0x5412u; 0x541Cu ]

let private both (name: string) (x64: int) (arm64: int) =
    {
        Name = name
        X64 = Some x64
        Arm64 = Some arm64
    }

let private x64Only (name: string) (x64: int) =
    {
        Name = name
        X64 = Some x64
        Arm64 = None
    }

/// Refused with `EPERM`: tracing and reading other processes, loading kernel code, mounting,
/// rebooting and the clocks, the kernel keyring, BPF, perf, userfaultfd, file handles, quotas,
/// the kernel log, raw I/O ports, joining other namespaces and chroot. Numbers from the kernel's
/// `unistd_64.h` (x86-64) and `asm-generic/unistd.h` (arm64).
let refused: Syscall list =
    [
        both "ptrace" 101 117
        both "process_vm_readv" 310 270
        both "process_vm_writev" 311 271
        both "kexec_load" 246 104
        both "kexec_file_load" 320 294
        both "init_module" 175 105
        both "finit_module" 313 273
        both "delete_module" 176 106
        both "mount" 165 40
        both "umount2" 166 39
        both "pivot_root" 155 41
        both "open_tree" 428 428
        both "move_mount" 429 429
        both "fsopen" 430 430
        both "fsconfig" 431 431
        both "fsmount" 432 432
        both "fspick" 433 433
        both "mount_setattr" 442 442
        both "swapon" 167 224
        both "swapoff" 168 225
        both "reboot" 169 142
        both "acct" 163 89
        both "settimeofday" 164 170
        both "clock_settime" 227 112
        both "adjtimex" 159 171
        both "clock_adjtime" 305 266
        x64Only "iopl" 172
        x64Only "ioperm" 173
        x64Only "uselib" 134
        both "add_key" 248 217
        both "request_key" 249 218
        both "keyctl" 250 219
        both "bpf" 321 280
        both "perf_event_open" 298 241
        both "userfaultfd" 323 282
        both "open_by_handle_at" 304 265
        both "name_to_handle_at" 303 264
        both "quotactl" 179 60
        both "lookup_dcookie" 212 18
        both "syslog" 103 116
        both "setns" 308 268
        both "chroot" 161 51
    ]

/// Refused with `ENOSYS`, so that libc falls back to an older call the filter can check:
/// `clone3` passes its flags in memory, which BPF cannot read; `clone` passes them in a register.
let unsupported: Syscall list = [ both "clone3" 435 435 ]

let private ioctl = both "ioctl" 16 29
let private unshare = both "unshare" 272 97
let private clone = both "clone" 56 220

let private numberOn (arch: Arch) (syscall: Syscall) : int option =
    match arch with
    | Arch.X64 -> syscall.X64
    | Arch.Arm64 -> syscall.Arm64

// struct seccomp_data: nr (u32) at 0, arch (u32) at 4, ip (u64) at 8, args (u64 × 6) from 16. Both
// architectures are little-endian, so an argument's low 32 bits are at its offset.
let private nrOffset = 0u
let private archOffset = 4u
let private argOffset (index: int) = uint32 (16 + 8 * index)

let private retAllow = 0x7FFF0000u
let private retErrno (errno: int) = 0x00050000u ||| uint32 errno
let private retKill = 0x80000000u

let private ld (offset: uint32) =
    {
        Code = 0x20us
        Jt = 0uy
        Jf = 0uy
        K = offset
    }

let private jeq (k: uint32) (jt: int) (jf: int) =
    {
        Code = 0x15us
        Jt = byte jt
        Jf = byte jf
        K = k
    }

let private jge (k: uint32) (jt: int) (jf: int) =
    {
        Code = 0x35us
        Jt = byte jt
        Jf = byte jf
        K = k
    }

let private jset (k: uint32) (jt: int) (jf: int) =
    {
        Code = 0x45us
        Jt = byte jt
        Jf = byte jf
        K = k
    }

let private ret (k: uint32) =
    {
        Code = 0x06us
        Jt = 0uy
        Jf = 0uy
        K = k
    }

/// The filter for an architecture, as instructions.
let instructions (arch: Arch) : Instruction list =
    let number (syscall: Syscall) = numberOn arch syscall

    let refuse (errno: int) (syscall: Syscall) =
        match number syscall with
        | Some nr -> [ jeq (uint32 nr) 0 1; ret (retErrno errno) ]
        | None -> []

    /// A call allowed unless one of its argument's low 32 bits match (`matches` jumps to refuse).
    let argCheck (syscall: Syscall) (body: Instruction list) =
        match number syscall with
        | Some nr ->
            // jeq nr → body; body ends with "allow; refuse".
            [ jeq (uint32 nr) 0 (body.Length + 2) ]
            @ body
            @ [ ret retAllow; ret (retErrno EPERM) ]
        | None -> []

    let ioctls =
        let count = refusedIoctls.Length

        ld (argOffset 1)
        :: (refusedIoctls
            |> List.mapi (fun i cmd ->
                // Equal: jump past the remaining checks and the allow, to the refusal.
                jeq cmd (count - i) 0))

    let newUser = [ ld (argOffset 0); jset CloneNewUser 1 0 ]

    [
        ld archOffset
        jeq (auditArch arch) 1 0
        ret retKill
        ld nrOffset
        if arch = Arch.X64 then
            jge X32Bit 0 1
            ret retKill
        yield! refused |> List.collect (refuse EPERM)
        yield! unsupported |> List.collect (refuse ENOSYS)
        yield! argCheck ioctl ioctls
        yield! argCheck unshare newUser
        yield! argCheck clone newUser
        ret retAllow
    ]

/// The filter for an architecture as bwrap reads it: `struct sock_filter` entries, little-endian.
let program (arch: Arch) : byte array =
    let instructions = instructions arch
    let bytes = Array.zeroCreate<byte>(8 * instructions.Length)

    instructions
    |> List.iteri (fun i ins ->
        let at = 8 * i
        bytes[at] <- byte (ins.Code &&& 0xFFus)
        bytes[at + 1] <- byte (ins.Code >>> 8)
        bytes[at + 2] <- ins.Jt
        bytes[at + 3] <- ins.Jf
        bytes[at + 4] <- byte (ins.K &&& 0xFFu)
        bytes[at + 5] <- byte ((ins.K >>> 8) &&& 0xFFu)
        bytes[at + 6] <- byte ((ins.K >>> 16) &&& 0xFFu)
        bytes[at + 7] <- byte (ins.K >>> 24))

    bytes

/// Runs a filter on a system call (`arch` as the kernel reports it, the call's number and its
/// arguments' low 32 bits), as the kernel would: what tests and the JIT-vs-native diff check.
let evaluate (program: Instruction list) (arch: uint32) (nr: uint32) (args: uint32 list) : Action =
    let code = Array.ofList program

    let load (offset: uint32) =
        match offset with
        | 0u -> nr
        | 4u -> arch
        | offset when offset >= 16u && (offset - 16u) % 8u = 0u ->
            args |> List.tryItem (int ((offset - 16u) / 8u)) |> Option.defaultValue 0u
        | other -> invalidArg (nameof program) $"no field at offset {other}"

    let mutable pc = 0
    let mutable a = 0u
    let mutable result = None

    while result.IsNone do
        if pc < 0 || pc >= code.Length then
            invalidArg (nameof program) "the program ran off its end"

        let ins = code[pc]

        let jump (taken: bool) =
            pc <- pc + 1 + int (if taken then ins.Jt else ins.Jf)

        match ins.Code with
        | 0x20us ->
            a <- load ins.K
            pc <- pc + 1
        | 0x15us -> jump (a = ins.K)
        | 0x35us -> jump (a >= ins.K)
        | 0x45us -> jump (a &&& ins.K <> 0u)
        | 0x06us ->
            result <-
                Some(
                    if ins.K = retAllow then
                        Action.Allow
                    elif ins.K = retKill then
                        Action.Kill
                    elif ins.K &&& 0xFFFF0000u = 0x00050000u then
                        Action.Errno(int (ins.K &&& 0xFFFFu))
                    else
                        invalidArg (nameof program) $"unknown return {ins.K}"
                )
        | other -> invalidArg (nameof program) $"unknown instruction {other}"

    result.Value

/// The number of a call on an architecture, for tests.
let numberOf (arch: Arch) (name: string) : uint32 option =
    refused @ unsupported @ [ ioctl; unshare; clone ]
    |> List.tryFind (fun s -> s.Name = name)
    |> Option.bind (numberOn arch)
    |> Option.map uint32

/// A short, stable name for a program's bytes (a SHA-256 prefix), for diagnostics and scenarios.
let digest (bytes: byte array) : string =
    Convert.ToHexStringLower(Security.Cryptography.SHA256.HashData bytes).Substring(0, 16)
