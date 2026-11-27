/// The `bun` extension runtime (`fx.exthost.bun`, T115): the extension host's files carried in the binary
/// (`ExthostFiles.fs`, generated here from packages/exthost-bun/src and packages/sdk/src), its manifest
/// (the TypeScript process serves the same one, packages/exthost-bun/test/fixtures/manifest.json), and
/// the TypeScript sample loaded through it for the parity suite: the extension host as a process
/// extension on the pinned Bun with `fx.runtime.bun`'s launch, running from its files written into a
/// package directory, and the sample as a package (`extension.js`, bundled from its module) on the
/// runtime it contributes. Without the pinned Bun (offline) those tests are skipped locally and fail in CI.
module Fx.Extensions.Tests.BunRuntime

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Fx.Contracts
open Fx.Effects
open Fx.Effects.Live
open Fx.Extensions
open Fx.Sdk
open Fx.Ext.Sample
open Fx.Ext.RuntimeBun
open Fx.Ext.ExthostBun
open Fx.Kernel.Tests
open Fx.Aot.Scenarios.Extensions
open Fx.Aot.Scenarios.Extensions.SampleHost

/// The TypeScript files of a directory below the repository root, recursively, without declaration
/// files (only imported as types, which Bun erases): paths relative to the root, with `/`.
let private typeScriptIn (segments: string list) : string list =
    let dir = Repo.path segments

    Directory.GetFiles(dir, "*.ts", SearchOption.AllDirectories)
    |> Array.filter (fun file -> not (file.EndsWith(".d.ts", StringComparison.Ordinal)))
    |> Array.map (fun file -> Path.GetRelativePath(Repo.root, file).Replace('\\', '/'))
    |> List.ofArray

/// The extension host's sources: its package's and the SDK's, by path.
let sources () : (string * string) list =
    typeScriptIn [ "packages"; "exthost-bun"; "src" ]
    @ typeScriptIn [ "packages"; "sdk"; "src" ]
    |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
    |> List.map (fun name -> name, File.ReadAllText(Repo.path (List.ofArray (name.Split '/'))))

let private literal (line: string) : string =
    let text = StringBuilder("\"")

    for c in line do
        match c with
        | '\\' -> text.Append "\\\\" |> ignore
        | '"' -> text.Append "\\\"" |> ignore
        | '\t' -> text.Append "\\t" |> ignore
        | c when c < ' ' || c = '\u007f' -> text.Append("\\u").Append((int c).ToString("x4")) |> ignore
        | c -> text.Append c |> ignore

    text.Append('"').ToString()

let private identifier (name: string) =
    String(
        name
        |> Seq.map (fun c -> if Char.IsAsciiLetterOrDigit c then c else '_')
        |> Array.ofSeq
    )

/// The F# source that carries the files: each file as an array of its lines (so that the formatter
/// keeps it as it is), joined with `\n`.
let render (files: (string * string) list) : string =
    let lines = ResizeArray<string>()

    lines.AddRange
        [
            "// Generated from packages/exthost-bun/src and packages/sdk/src by tests/Fx.Extensions.Tests. Do not"
            "// edit: change the sources and run the tests with UPDATE_GOLDEN=1."
            ""
            "/// The Bun extension host's files (packages/exthost-bun/src and the SDK it runs on, without the"
            "/// generated declaration files), by their paths in the package directory, as lines joined with `\\n`"
            "/// (the repository checks them out with `\\n`, .gitattributes)."
            "module Fx.Ext.ExthostBun.ExthostFiles"
            ""
        ]

    for name, content in files do
        lines.Add $"let private {identifier name} ="
        lines.Add "    [|"

        for line in content.Split '\n' do
            lines.Add("        " + literal line)

        lines.Add "    |]"
        lines.Add ""

    lines.Add "/// Every file: its path and content."
    lines.Add "let all: (string * string) list ="
    lines.Add "    ["

    for name, _ in files do
        lines.Add $"        \"{name}\", String.concat \"\\n\" {identifier name}"

    lines.Add "    ]"
    String.Join("\n", lines) + "\n"

/// The extension host's manifest on `fx.runtime.bun`.
let manifest = ExthostBun.manifestOn RuntimeBun.runtime

/// `packages/exthost-bun/test/fixtures/manifest.json`: the manifest the TypeScript process serves is
/// this one without its launch.
let renderManifest () : string =
    Json.serializeIndented (Json.Object [ "manifest", Manifest.codec.Encode manifest ])
    + "\n"

/// The pinned Bun, or a skipped test (a failed one in CI).
let private pinnedBun () : Task<string> =
    task {
        match! PinnedBun.executable () with
        | Ok bun -> return bun
        | Error reason when Environment.GetEnvironmentVariable "CI" = "true" ->
            return failtest $"the pinned Bun is not available: {reason}"
        | Error reason -> return skiptest $"the pinned Bun is not available: {reason}"
    }

let private counter = ref 0

let private fresh (name: string) : string =
    let n = Interlocked.Increment counter

    let dir =
        Repo.path [ "artifacts"; "exthost-bun"; $"{Environment.ProcessId}-{n}"; name ]

    Directory.CreateDirectory dir |> ignore
    dir

/// The extension host's package directory: its files as the binary carries them, and the directories
/// its launch points Bun at.
let private exthostRoot () : string =
    let root = fresh "exthost"

    for dir in ExthostBun.directories do
        Directory.CreateDirectory(Path.Combine(root, dir)) |> ignore

    for file in ExthostBun.files RuntimeBun.runtime do
        let path = Path.Combine(root, file.Name.Replace('/', Path.DirectorySeparatorChar))
        Directory.CreateDirectory(Path.GetDirectoryName path |> nonNull) |> ignore
        File.WriteAllText(path, file.Content)

    root

/// The sample as a package for the `bun` runtime: its module bundled into `extension.js`.
let private samplePackage (bun: string) : Task<string> =
    task {
        let root = fresh "sample"

        do!
            TypeScriptSample.bundle
                bun
                (Repo.path [ "packages"; "sample-extension"; "src"; "sample.ts" ])
                (Path.Combine(root, ExthostBun.EntryFile))

        return root
    }

let private remove (path: string) =
    try
        Directory.Delete(path, true)
    with _ ->
        ()

/// The TypeScript sample on the `bun` runtime: the extension host and the sample, through the
/// bootstrap's process and hosted phases. The pinned Bun stands for the installed component.
let load () : Task<Loaded> =
    task {
        let! bun = pinnedBun ()
        let exthost = exthostRoot ()
        let! sample = samplePackage bun

        let env =
            hostEnv (LocalShell.create (LocalShell.defaultShell ()) AppContext.BaseDirectory)

        // The pinned Bun stands for the installed component: the launch's `${bin.bun}` is its path.
        let installed: InstalledComponent =
            {
                Id = RuntimeBun.runtime.Component.Id
                Version = SemVer.tryParse ExthostBun.requires.Head.Version |> Option.get
                Dir = Path.GetDirectoryName bun |> nonNull
                Bin = [ "bun", bun ]
                Env = []
            }

        let settings =
            { settings with
                InstallComponents = fun _ -> EffResult.ok [ installed ]
            }

        let! host =
            within (
                Eff.run
                    env
                    (ExtensionHost.bootstrap
                        settings
                        [
                            Candidate.create Source.Builtin (Package.Manifest(manifest, Some exthost))
                            Candidate.create
                                Source.Builtin
                                (Package.Manifest(
                                    { Sample.manifest with
                                        Runtime = ExthostBun.RuntimeName
                                    },
                                    Some sample
                                ))
                        ])
            )

        return
            {
                Host = host
                Env = env
                Dispose =
                    fun () ->
                        task {
                            do! within (Eff.run env (ExtensionHost.stop host))
                            remove (Path.GetDirectoryName exthost |> nonNull)
                        }
            }
    }

[<Tests>]
let tests =
    testList
        "fx.exthost.bun"
        [
            test "ExthostFiles.fs is generated from the extension host's sources" {
                Repo.expectGenerated [ "extensions"; "Fx.Ext.ExthostBun"; "ExthostFiles.fs" ] (render (sources ()))
            }

            test "the files are the sources, byte for byte, with the entry point" {
                Expect.equal (ExthostBun.sources |> List.map (fun f -> f.Name, f.Content)) (sources ()) "the sources"
                Expect.isTrue (ExthostBun.sources |> List.exists (fun f -> f.Name = ExthostBun.Main)) "main.ts"
            }

            test "the TypeScript process declares this manifest" {
                Repo.expectGenerated
                    [ "packages"; "exthost-bun"; "test"; "fixtures"; "manifest.json" ]
                    (renderManifest ())
            }

            test "the manifest checks, and its launch is fx.runtime.bun's, running main.ts in the package directory" {
                Expect.isEmpty (Manifest.check manifest) "issues"

                match manifest.Launch with
                | None -> failtest "no launch"
                | Some launch ->
                    Expect.equal launch.Exe RuntimeBun.launch.Exe "the runtime's Bun"
                    Expect.equal (List.last launch.Args) "${root}/packages/exthost-bun/src/main.ts" "the program"
                    Expect.equal launch.EnvDeny RuntimeBun.launch.EnvDeny "the denied variables"

                    let templates =
                        launch.Args @ launch.UntrustedOverrides @ (launch.EnvSet |> List.map snd)

                    for text in templates do
                        Expect.isFalse (text.Contains "${owned.") $"{text}: owned directories are in the package"
                        Expect.isFalse (text.Contains "${file.") $"{text}: files are in the package"

                    for file in RuntimeBun.runtime.Files do
                        Expect.isTrue
                            (ExthostBun.files RuntimeBun.runtime
                             |> List.exists (fun f -> f.Name = "config/" + file.Name))
                            $"{file.Name} is in config/"
            }

            test "it contributes the runtime bun and requires the pinned Bun" {
                Expect.equal
                    (manifest.Contributions |> List.map (fun c -> c.Type, c.Id))
                    [ ExtensionRuntimes.contributionType.Name, "bun" ]
                    "contributions"

                Expect.equal
                    (manifest.Requires |> List.map (fun r -> r.Id, r.Version))
                    [ "bun", Pins.Version ]
                    "requires"
            }
        ]
