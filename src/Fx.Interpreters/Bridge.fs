namespace Fx.Interpreters

open System
open System.Text.RegularExpressions
open Fx
open Fx.Contracts
open Fx.Effects
open Fx.Kernel
open Fx.Sdk

/// How the host takes a cell's `finish` (spec §4.5, §6.5): the result's schema (for the bridge's
/// declarations) and its validation, the result's codec and then the checks (`Ensure`). Evidence stays
/// the values the cell passed, never retyped.
[<NoEquality; NoComparison>]
type Finishing<'R> =
    {
        Schema: Schema
        Validate: Finish -> Eff<'R, Result<unit, FinishRejected>>
    }

    override this.ToString() =
        $"finishing with {Schema.render this.Schema}"

/// The host bridge of a session (spec §6.5): the global, the bindings, descriptions of their groups,
/// and how `finish` is validated. Everything the kernel's `initialize` and declarations need comes
/// from it, so what a cell can call, what its declarations say and what the host accepts agree.
[<NoEquality; NoComparison>]
type Bridge<'R> =
    {
        /// The global in the kernel: `Fx.Product.kernelGlobal`.
        Global: string
        Description: string option
        Groups: (string * string) list
        Bindings: HostBinding<'R> list
        Finish: Finishing<'R>
    }

    override this.ToString() =
        $"bridge {this.Global} ({this.Bindings.Length} bindings)"

[<RequireQualifiedAccess>]
module Finishing =
    /// Decodes a `finish` result with `codec`, runs the checks, then hands the result to `accept`
    /// (which records it for the turn). A rejection reaches the cell with the issues.
    let create (codec: Codec<'T>) (ensure: ('T -> Issue list) list) (accept: 'T -> Eff<'R, unit>) : Finishing<'R> =
        {
            Schema = codec.Schema
            Validate =
                fun finish ->
                    match KernelProtocol.Finish.validate codec ensure finish with
                    | Ok result -> accept result |> Eff.map Ok
                    | Error rejected -> EffResult.error rejected
        }

[<RequireQualifiedAccess>]
module Bridge =
    /// The names the kernel keeps for itself on the global.
    let reserved = [ "finish"; "activity" ]

    let private segment =
        Regex("^[A-Za-z_$][A-Za-z0-9_$]*$", RegexOptions.CultureInvariant)

    /// A bridge with the product's global and no bindings.
    let create (finish: Finishing<'R>) : Bridge<'R> =
        {
            Global = Product.kernelGlobal
            Description = Some "The host bridge: what cells can call, and the types it uses."
            Groups = []
            Bindings = []
            Finish = finish
        }

    /// Adds bindings and the descriptions of their groups.
    let add (groups: (string * string) list) (bindings: HostBinding<'R> list) (bridge: Bridge<'R>) : Bridge<'R> =
        { bridge with
            Groups = bridge.Groups @ groups
            Bindings = bridge.Bindings @ bindings
        }

    /// What interpreters see: the declarations of the bindings and the result `finish` takes.
    let surface (bridge: Bridge<'R>) : BindingSurface =
        {
            Global = bridge.Global
            Description = bridge.Description
            Groups = bridge.Groups
            Bindings = bridge.Bindings |> List.map (fun b -> b.Decl)
            Finish = bridge.Finish.Schema
        }

    /// The `kernel` member of `initialize` for this bridge.
    let setup (bridge: Bridge<'R>) (limits: KernelLimits) (moduleBase: string option) : KernelSetup =
        {
            Versions = KernelProtocol.versions
            Global = bridge.Global
            Bindings = bridge.Bindings |> List.map (fun b -> b.Decl.Path)
            ModuleBase = moduleBase
            Limits = limits
        }

    /// The host's side of the kernel protocol for this bridge: calls dispatched to the bindings,
    /// `finish` validated, output and activity to the handlers given.
    let host
        (bridge: Bridge<'R>)
        (output: CellOutput -> Eff<'R, unit>)
        (activity: CellActivity -> Eff<'R, unit>)
        : KernelHost<'R> =
        {
            HostCall = HostBinding.dispatch bridge.Bindings
            Finish = bridge.Finish.Validate
            Output = output
            Activity = activity
        }

    /// Everything wrong with a bridge: paths that are not dotted identifiers, the kernel's reserved
    /// names, a path given twice, a path that is both a binding and a group.
    let validate (bridge: Bridge<'R>) : Issue list =
        let paths = bridge.Bindings |> List.map (fun b -> b.Decl.Path)

        [
            if not (segment.IsMatch bridge.Global) then
                Issue.at [ PathSegment.Key "global" ] $"{Json.quote bridge.Global} is not an identifier"

            for i, path in List.indexed paths do
                let at = [ PathSegment.Key "bindings"; PathSegment.Index i ]
                let segments = path.Split '.'

                if segments |> Array.exists (fun s -> not (segment.IsMatch s)) then
                    Issue.at at $"{Json.quote path} is not a dotted path of identifiers"
                elif List.contains segments[0] reserved then
                    Issue.at at $"{Json.quote segments[0]} is the kernel's own name on the global"

                if
                    paths
                    |> List.exists (fun other -> other.StartsWith(path + ".", StringComparison.Ordinal))
                then
                    Issue.at at $"{Json.quote path} is both a binding and a group"

            for dup in paths |> List.countBy id |> List.filter (fun (_, n) -> n > 1) |> List.map fst do
                Issue.at [ PathSegment.Key "bindings" ] $"{Json.quote dup} is bound twice"
        ]

/// Cells as models write them (spec §19.2).
[<RequireQualifiedAccess>]
module Cell =
    let private intentLine =
        Regex(@"^\s*//\s*intent:\s*(.*?)\s*$", RegexOptions.CultureInvariant)

    /// The cell's `// intent:` line: its first line that is not blank, if it is one. The kernel gets
    /// it in `cell/run`; it does not change what the cell does.
    let intent (code: string) : string option =
        code.Split('\n')
        |> Array.map (fun line -> line.TrimEnd '\r')
        |> Array.tryFind (fun line -> not (String.IsNullOrWhiteSpace line))
        |> Option.bind (fun line ->
            let m = intentLine.Match line

            if m.Success && m.Groups[1].Value <> "" then
                Some m.Groups[1].Value
            else
                None)
