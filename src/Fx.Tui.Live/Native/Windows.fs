/// kernel32 on Windows: the console's modes, code pages, input records, output and size.
///
/// Every signature is blittable (`BOOL` as `int`, handles and pointers as `nativeint`); structures
/// are read from native memory at fixed offsets.
module internal Fx.Tui.Live.Native.Windows

open System.Runtime.InteropServices

[<Literal>]
let private Kernel32 = "kernel32.dll"

let StdInputHandle = -10
let StdOutputHandle = -11

// Input modes.
let EnableProcessedInput = 0x0001u
let EnableLineInput = 0x0002u
let EnableEchoInput = 0x0004u
let EnableWindowInput = 0x0008u
let EnableMouseInput = 0x0010u
let EnableVirtualTerminalInput = 0x0200u

// Output modes.
let EnableProcessedOutput = 0x0001u
let EnableVirtualTerminalProcessing = 0x0004u

let Utf8CodePage = 65001u
let WaitObject0 = 0u

/// `INPUT_RECORD`: 20 bytes; `EventType` (a `WORD`) at 0, the event from 4.
let InputRecordSize = 20
let KeyEvent = 1s

/// In a `KEY_EVENT_RECORD` (from offset 4 of the record): `bKeyDown` (a `BOOL`), `wRepeatCount`,
/// `wVirtualKeyCode`, `wVirtualScanCode`, `uChar` (a `WCHAR`), `dwControlKeyState`.
module KeyRecord =
    let KeyDown = 4
    let RepeatCount = 8
    let Char = 14

/// `CONSOLE_SCREEN_BUFFER_INFO`: 22 bytes; `srWindow` (Left, Top, Right, Bottom as `SHORT`s) at 10.
module ScreenInfo =
    let Size = 22
    let Left = 10
    let Top = 12
    let Right = 14
    let Bottom = 16

[<DllImport(Kernel32)>]
extern nativeint GetStdHandle(int handle)

[<DllImport(Kernel32)>]
extern int GetConsoleMode(nativeint handle, nativeint mode)

[<DllImport(Kernel32)>]
extern int SetConsoleMode(nativeint handle, uint32 mode)

[<DllImport(Kernel32)>]
extern uint32 GetConsoleCP()

[<DllImport(Kernel32)>]
extern int SetConsoleCP(uint32 codePage)

[<DllImport(Kernel32)>]
extern uint32 GetConsoleOutputCP()

[<DllImport(Kernel32)>]
extern int SetConsoleOutputCP(uint32 codePage)

[<DllImport(Kernel32)>]
extern uint32 WaitForSingleObject(nativeint handle, uint32 milliseconds)

[<DllImport(Kernel32)>]
extern int GetNumberOfConsoleInputEvents(nativeint handle, nativeint count)

[<DllImport(Kernel32)>]
extern int ReadConsoleInputW(nativeint handle, nativeint buffer, uint32 length, nativeint read)

[<DllImport(Kernel32)>]
extern int WriteConsoleW(nativeint handle, nativeint buffer, uint32 length, nativeint written, nativeint reserved)

[<DllImport(Kernel32)>]
extern int GetConsoleScreenBufferInfo(nativeint handle, nativeint info)
