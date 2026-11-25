/// Which OS account is at the other end of a local connection, as the operating system reports it
/// (spec §18; the proof of `fx.identity.local-os-user`, docs/modules/identity.md), and which account
/// this process runs as. The listener's platform code, an imperative shell with its own entry in
/// analyzers.json: socket options on Unix, the client process's token on Windows.
///
/// | Transport | Linux | macOS | Windows |
/// |---|---|---|---|
/// | Unix socket | `SO_PEERCRED` | `LOCAL_PEERCRED`, `LOCAL_PEERPID` | `SIO_AF_UNIX_GETPEERPID`, then the process's token |
/// | Named pipe | — | — | `GetNamedPipeClientProcessId`, then the process's token |
/// | Loopback TCP | none | none | none |
module Fx.Transport.Http.PeerCredentials

open System
open System.Buffers.Binary
open System.IO.Pipes
open System.Net.Sockets
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open System.Security.Principal
open Microsoft.AspNetCore.Connections.Features
open Microsoft.AspNetCore.Http

// Every P/Invoke is blittable: a signature that needs marshalling fails on the JIT as under Native AOT.
[<assembly: DisableRuntimeMarshalling>]
do ()

/// The account of a peer process.
type PeerAccount =
    {
        /// The decimal user id on Unix (the effective one), the user's SID on Windows: the same form
        /// as `currentAccount ()`.
        Account: string
        /// The peer's process id, when the system says. For diagnostics only: ids are reused.
        Process: int option
    }

    override this.ToString() =
        match this.Process with
        | Some pid -> this.Account + " (process " + string pid + ")"
        | None -> this.Account

module private Unix =
    [<DllImport("libc")>]
    extern uint32 geteuid()

    // Linux, the same on x64 and arm64, glibc and musl: struct ucred { pid_t pid; uid_t uid; gid_t gid; }.
    [<Literal>]
    let SolSocket = 1

    [<Literal>]
    let SoPeerCred = 17

    // macOS: struct xucred { u_int cr_version; uid_t cr_uid; short cr_ngroups; gid_t cr_groups[16]; }.
    [<Literal>]
    let SolLocal = 0

    [<Literal>]
    let LocalPeerCred = 1

    [<Literal>]
    let LocalPeerPid = 2

    [<Literal>]
    let XucredSize = 76

    let peer (socket: Socket) : PeerAccount option =
        if OperatingSystem.IsLinux() then
            let buffer = Array.zeroCreate<byte> 12

            if socket.GetRawSocketOption(SolSocket, SoPeerCred, Span buffer) = 12 then
                Some
                    {
                        Account = string (BinaryPrimitives.ReadUInt32LittleEndian(ReadOnlySpan(buffer, 4, 4)))
                        Process = Some(BinaryPrimitives.ReadInt32LittleEndian(ReadOnlySpan(buffer, 0, 4)))
                    }
            else
                None
        elif OperatingSystem.IsMacOS() then
            let credentials = Array.zeroCreate<byte> XucredSize
            let length = socket.GetRawSocketOption(SolLocal, LocalPeerCred, Span credentials)

            // cr_version 0 (XUCRED_VERSION) is the only layout there is.
            if
                length >= 8
                && BinaryPrimitives.ReadUInt32LittleEndian(ReadOnlySpan(credentials, 0, 4)) = 0u
            then
                let pid = Array.zeroCreate<byte> 4

                let processId =
                    if socket.GetRawSocketOption(SolLocal, LocalPeerPid, Span pid) = 4 then
                        Some(BinaryPrimitives.ReadInt32LittleEndian(ReadOnlySpan pid))
                    else
                        None

                Some
                    {
                        Account = string (BinaryPrimitives.ReadUInt32LittleEndian(ReadOnlySpan(credentials, 4, 4)))
                        Process = processId
                    }
            else
                None
        else
            None

module private Windows =
    [<Literal>]
    let ProcessQueryLimitedInformation = 0x1000u

    [<Literal>]
    let TokenQuery = 0x0008u

    [<Literal>]
    let TokenUser = 1

    /// `SIO_AF_UNIX_GETPEERPID`: `_WSAIOR(IOC_VENDOR, 256)`.
    [<Literal>]
    let SioAfUnixGetPeerPid = 0x58000100

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern nativeint OpenProcess(uint32 access, int inheritHandle, uint32 processId)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern int CloseHandle(nativeint handle)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern int GetNamedPipeClientProcessId(nativeint pipe, nativeint processId)

    [<DllImport("advapi32.dll", SetLastError = true)>]
    extern int OpenProcessToken(nativeint processHandle, uint32 desiredAccess, nativeint tokenHandle)

    [<DllImport("advapi32.dll", SetLastError = true)>]
    extern int GetTokenInformation(nativeint token, int infoClass, nativeint info, uint32 length, nativeint returned)

    /// The SID of the user a process runs as; `None` if the process is gone or not ours to query.
    let accountOf (processId: uint32) : string option =
        let handles = Marshal.AllocHGlobal 16

        try
            let tokenSlot = handles
            let sizeSlot = handles + nativeint 8
            Marshal.WriteIntPtr(tokenSlot, 0n)
            Marshal.WriteInt32(sizeSlot, 0)
            let proc = OpenProcess(ProcessQueryLimitedInformation, 0, processId)

            if proc = 0n then
                None
            else
                try
                    if OpenProcessToken(proc, TokenQuery, tokenSlot) = 0 then
                        None
                    else
                        let token = Marshal.ReadIntPtr tokenSlot

                        try
                            // The first call fails with ERROR_INSUFFICIENT_BUFFER and says the size.
                            GetTokenInformation(token, TokenUser, 0n, 0u, sizeSlot) |> ignore
                            let size = Marshal.ReadInt32 sizeSlot

                            if size <= 0 then
                                None
                            else
                                let info = Marshal.AllocHGlobal size

                                try
                                    if GetTokenInformation(token, TokenUser, info, uint32 size, sizeSlot) = 0 then
                                        None
                                    else
                                        // TOKEN_USER { SID_AND_ATTRIBUTES User { PSID Sid; DWORD Attributes } }
                                        let sid = SecurityIdentifier(Marshal.ReadIntPtr info)
                                        Some sid.Value
                                finally
                                    Marshal.FreeHGlobal info
                        finally
                            CloseHandle token |> ignore
                finally
                    CloseHandle proc |> ignore
        finally
            Marshal.FreeHGlobal handles

    let ofProcess (processId: uint32) : PeerAccount option =
        accountOf processId
        |> Option.map (fun account ->
            {
                Account = account
                Process = Some(int processId)
            })

    let pipeClient (pipe: NamedPipeServerStream) : PeerAccount option =
        let slot = Marshal.AllocHGlobal 4

        try
            Marshal.WriteInt32(slot, 0)
            let handle = pipe.SafePipeHandle.DangerousGetHandle()

            if GetNamedPipeClientProcessId(handle, slot) = 0 then
                None
            else
                ofProcess (uint32 (Marshal.ReadInt32 slot))
        finally
            Marshal.FreeHGlobal slot

    let socketPeer (socket: Socket) : PeerAccount option =
        let pid = Array.zeroCreate<byte> 4

        try
            if socket.IOControl(SioAfUnixGetPeerPid, null, pid) = 4 then
                ofProcess (BinaryPrimitives.ReadUInt32LittleEndian(ReadOnlySpan pid))
            else
                None
        with :? SocketException ->
            None

/// The account this process runs as, in the form peers are reported in: the effective user id on
/// Unix, the user's SID on Windows.
let currentAccount () : string =
    if OperatingSystem.IsWindows() then
        use identity = WindowsIdentity.GetCurrent()

        match identity.User with
        | null -> invalidOp "The process has no user SID."
        | sid -> sid.Value
    else
        string (Unix.geteuid ())

/// The account of the process at the other end of a connected Unix domain socket, if the system says.
let ofUnixSocket (socket: Socket) : PeerAccount option =
    if socket.AddressFamily <> AddressFamily.Unix then
        None
    elif OperatingSystem.IsWindows() then
        Windows.socketPeer socket
    else
        Unix.peer socket

/// The account of a named pipe's client process (Windows), if the system says.
let ofNamedPipe (pipe: NamedPipeServerStream) : PeerAccount option =
    if OperatingSystem.IsWindows() && pipe.IsConnected then
        Windows.pipeClient pipe
    else
        None

/// The account of the peer of a request's connection on a Kestrel listener: Unix sockets and named
/// pipes say; loopback TCP does not (`None`).
let ofContext (context: HttpContext) : PeerAccount option =
    match context.Features.Get<IConnectionNamedPipeFeature>() with
    | null ->
        match context.Features.Get<IConnectionSocketFeature>() with
        | null -> None
        | feature -> ofUnixSocket feature.Socket
    | feature -> ofNamedPipe feature.NamedPipe
