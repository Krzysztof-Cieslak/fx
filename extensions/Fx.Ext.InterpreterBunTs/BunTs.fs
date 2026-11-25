/// `fx.interpreter.bun-ts` (spec §6.3): TypeScript cells in a persistent kernel on the pinned Bun.
/// A native extension that contributes one `InterpreterProvider`: the kernel's files (carried in the
/// binary, `KernelFiles`), its launch on `fx.runtime.bun`'s runtime, the host bridge's declarations
/// (`<global>.d.ts`, generated from the bindings' codecs), the prompt pack and `bun check`. See
/// README.md and docs/modules/interpreters.md.
module Fx.Ext.InterpreterBunTs.BunTs

open System
open Fx
open Fx.Contracts
open Fx.Contracts.TypeScript
open Fx.Effects
open Fx.Sdk

/// The extension's id: `fx.interpreter.bun-ts`.
let id = Product.extensionId "interpreter.bun-ts"

/// The interpreter's id, the value of the interpreter variant.
[<Literal>]
let InterpreterId = "bun-ts"

/// The runtime it runs on: `fx.runtime.bun`'s.
[<Literal>]
let RuntimeId = "bun"

/// The Bun it needs: the pinned one (the kernel relies on `Bun.Transpiler`'s replMode, `bun check`
/// and `Bun.ModuleGraph` as 1.4.5 has them).
let requires: ComponentRef list = [ { Id = RuntimeId; Version = "1.4.5" } ]

/// What the kernel can do: no snapshots (Jupyter semantics plus the journal, spec §6.5), awaited
/// host calls, no packages yet.
let caps: InterpreterCaps =
    {
        Snapshot = false
        AsyncHostCalls = AsyncHostCalls.Await
        Packages = None
        Deterministic = false
    }

/// The kernel's entry point, and the directory with the bridge's declarations for `bun check`.
let launch: InterpreterLaunch =
    {
        Runtime = RuntimeId
        Program = [ "${asset.main.ts}"; "--bindings=${bindings}" ]
    }

/// The kernel's files, from `KernelFiles` (generated from packages/kernel-bun-ts/src).
let assets: RuntimeFile list =
    KernelFiles.all
    |> List.map (fun (name, content) -> { Name = name; Content = content })

let private nullSchema = Schema.ofKind SchemaKind.Null

/// The bridge's declarations as a global script: `declare namespace <global> { … }` with every
/// binding (`shell.run` in `const shell: { run(…) }`), `finish`, and the types they use.
let declarations (surface: BindingSurface) : string =
    let methods =
        surface.Bindings
        |> List.map (fun binding ->
            ({
                Name = binding.Path
                Description = binding.Description
                Parameters =
                    binding.Params
                    |> List.map (fun p ->
                        ({
                            Name = p.Name
                            Description = p.Description
                            Schema = p.Schema
                            Optional = p.Optional
                        }
                        : Parameter))
                Returns = binding.Returns
            }
            : Method))

    let finish: Method =
        {
            Name = "finish"
            Description =
                Some
                    "Ends the turn with a result the host checks. If it is rejected, this throws with the reasons (code finishRejected): fix the result and call it again."
            Parameters = [ Parameter.required "result" surface.Finish ]
            Returns = nullSchema
        }

    // Only groups that have bindings (a session may leave a group out).
    let groups =
        surface.Groups
        |> List.filter (fun (group, _) ->
            surface.Bindings
            |> List.exists (fun b -> b.Path.StartsWith(group + ".", StringComparison.Ordinal)))

    DeclarationFile.render
        {
            Layout = Layout.Global(surface.Global, surface.Description)
            Types = []
            Members = Member.ofPaths groups (finish :: methods)
        }

/// The binding files: `<global>.d.ts`, which the kernel's checker reads, and its text without the
/// generated-file header for the prompt.
let bindings (surface: BindingSurface) : BindingFiles =
    let text = declarations surface

    let header = DeclarationFile.Header + "\n\n"

    {
        Files =
            [
                {
                    Name = surface.Global + ".d.ts"
                    Content = text
                }
            ]
        Declarations =
            if text.StartsWith(header, StringComparison.Ordinal) then
                text.Substring header.Length
            else
                text
    }

let private withGlobal (text: string) =
    text.Replace("{global}", Product.kernelGlobal)

/// How the model writes cells (spec §6.9, §6.10, §19.2).
let promptPack: PromptPack =
    {
        Fence = "ts"
        Instructions =
            withGlobal (
                String.concat
                    "\n"
                    [
                        "You act by writing TypeScript cells. They run one at a time in a persistent kernel (Bun): top-level declarations (`const`, `let`, functions, types) stay defined for later cells, and top-level `await` works."
                        ""
                        "- Start every cell with one line `// intent: <what the cell is for>`."
                        "- Work through the `{global}` bridge declared below, e.g. `await {global}.shell.run(\"git status\")` or `await {global}.fs.read(\"src/app.ts\")`. Paths are relative to the workspace. The host checks every call: a call that fails throws an error with a `code`: `invalidArguments` (with the reasons), or `failed`, whose `data.code` says why (e.g. `notFound`)."
                        "- The value of a cell's last expression comes back to you as a short preview and stays available as `Out[\"<cell id>\"]`. Keep large values in variables and pass them on by name instead of printing them; printed output beyond a limit is cut in the middle."
                        "- `process`, `Bun` and `require` are not globals: import what you need, e.g. `const path = await import(\"node:path\")`."
                        "- End the turn with `await {global}.finish(result)`, with a result of the type `finish` declares. The host checks it; if it is rejected, the call throws with the reasons: fix the result and finish again."
                    ]
            )
        Examples =
            [
                {
                    Title = "Look around, keeping what you find in variables:"
                    Code =
                        withGlobal (
                            String.concat
                                "\n"
                                [
                                    "// intent: find the tests that mention retries"
                                    "const found = await {global}.shell.run(\"git grep -n retry -- tests\")"
                                    "found.stdout"
                                ]
                        )
                }
                {
                    Title = "Change a file and check the change:"
                    Code =
                        withGlobal (
                            String.concat
                                "\n"
                                [
                                    "// intent: rename the helper and run its tests"
                                    "const file = await {global}.fs.read(\"src/retry.ts\")"
                                    "await {global}.fs.write(file.path, file.text.replaceAll(\"retryOld\", \"retryNew\"))"
                                    "const run = await {global}.shell.run(\"bun test test/retry.test.ts\")"
                                    "run.exitCode"
                                ]
                        )
                }
                {
                    Title = "Finish the turn:"
                    Code =
                        withGlobal (
                            String.concat
                                "\n"
                                [
                                    "// intent: report what was done"
                                    "await {global}.finish({ kind: \"done\", summary: \"Renamed retryOld to retryNew; its tests pass.\", verify: \"bun test test/retry.test.ts\" })"
                                ]
                        )
                }
            ]
    }

/// The interpreter.
let provider: InterpreterProvider =
    {
        Id = InterpreterId
        Language = "typescript"
        Description =
            "TypeScript cells in a persistent kernel on the pinned Bun (node:vm context, Bun.Transpiler replMode, top-level await), type-checked with bun check."
        Requires = requires
        Launch = launch
        Assets = assets
        Bindings = bindings
        PromptPack = promptPack
        Check = Some CheckSpec.Kernel
        Caps = caps
    }

/// The package's manifest: native (the provider's bindings are a function), contributing the
/// interpreter. Its components are installed in the environment when a kernel starts there, not at
/// the host's bootstrap.
let manifest: Manifest =
    { Manifest.create id "1.0.0" Runtimes.Native with
        Description =
            Some
                "TypeScript cells on the pinned Bun: the bun-ts kernel, its launch, its bindings' declarations and prompt."
        Contributions = [ Declare.contribution InterpreterProviders.contributionType InterpreterId ]
    }

/// The package as a native extension.
let extension: Extension =
    {
        Manifest = manifest
        Activate =
            eff {
                return
                    Activation.create
                        [
                            Contribution.create InterpreterProviders.contributionType InterpreterId provider
                        ]
                        []
            }
    }

/// Every issue with the package's data: its manifest and its interpreter. Empty for what ships.
let check () : Issue list =
    Manifest.check manifest @ InterpreterProvider.validate provider
