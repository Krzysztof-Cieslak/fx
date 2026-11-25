namespace Fx.Sdk

open System

/// An environment variable that makes a runtime load code or options before the program it runs
/// does (spec §18), so that whoever sets it controls the program. Launch specs deny every one of
/// them (`InjectionVectors.all`).
type InjectionVector =
    {
        /// A variable name, or a prefix ending in `*`.
        Pattern: string
        /// Who reads it.
        Reader: string
        /// What it does.
        Effect: string
    }

    override this.ToString() = $"{this.Pattern} ({this.Reader})"

/// Patterns of environment variable names: an exact name, or a prefix ending in `*`.
[<RequireQualifiedAccess>]
module EnvPattern =
    let private comparison (ignoreCase: bool) =
        if ignoreCase then
            StringComparison.OrdinalIgnoreCase
        else
            StringComparison.Ordinal

    /// Whether a variable name matches a pattern. `ignoreCase`: Windows compares variable names
    /// without case.
    let matches (ignoreCase: bool) (pattern: string) (name: string) : bool =
        if pattern.EndsWith '*' then
            name.StartsWith(pattern.Substring(0, pattern.Length - 1), comparison ignoreCase)
        else
            String.Equals(name, pattern, comparison ignoreCase)

    /// Whether every name that `narrow` matches is matched by `broad` too, without case: `BUN_*`
    /// covers `BUN_INSPECT*` and `BUN_OPTIONS`.
    let covers (broad: string) (narrow: string) : bool =
        if broad.EndsWith '*' then
            let prefix = broad.Substring(0, broad.Length - 1)
            narrow.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        else
            not (narrow.EndsWith '*')
            && String.Equals(broad, narrow, StringComparison.OrdinalIgnoreCase)

/// The runtime injection vectors the product knows (spec §18). Launch specs must deny all of them
/// (`LaunchSpec.validate`), and the `process` runtime removes them whatever a manifest says. Rows
/// marked "positive control" have a test that shows the injection working on a plain launch of the
/// pinned Bun and failing on the hardened one (docs/spikes/bun-kernel.md, docs/modules/runtimes.md).
[<RequireQualifiedAccess>]
module InjectionVectors =
    let private vector (pattern: string) (reader: string) (effect: string) : InjectionVector =
        {
            Pattern = pattern
            Reader = reader
            Effect = effect
        }

    let all: InjectionVector list =
        [
            vector "BUN_OPTIONS" "Bun" "extra command-line options, e.g. --preload=<module> (positive control)"
            vector
                "BUN_INSPECT*"
                "Bun"
                "opens a debugger listener or connects to one: remote code execution (positive control)"
            vector "BUN_CONFIG_*" "Bun" "configuration overrides: registry, fetch logging, …"
            vector "BUN_JSC_*" "Bun" "JavaScriptCore options (positive control)"
            vector "BUN_FEATURE_FLAG_*" "Bun" "switches internal behaviour"
            vector "BUN_DEBUG*" "Bun" "debug logging and hooks"
            vector
                "NODE_OPTIONS"
                "Node.js"
                "options such as --require and --import; Bun ignores it, its Node children do not"
            vector "NODE_INSPECT*" "Node.js" "debugger settings"
            vector "NODE_PATH" "Node.js, Bun" "extra module resolution roots (positive control)"
            vector "NODE_CHANNEL_*" "Node.js, Bun" "an IPC channel the process trusts as its parent's"
            vector "NODE_REPL_EXTERNAL_MODULE" "Node.js" "replaces the REPL with a module"
            vector "NODE_TLS_REJECT_UNAUTHORIZED" "Node.js, Bun" "turns off TLS certificate checks"
            vector "NPM_CONFIG_*" "npm, Bun's installer" "registry, scripts and other package-manager settings"
            vector "DOTNET_STARTUP_HOOKS" ".NET" "loads assemblies before Main"
            vector "DOTNET_ADDITIONAL_DEPS" ".NET" "adds dependencies to the application"
            vector "DOTNET_SHARED_STORE" ".NET" "loads assemblies from another store"
            vector "DOTNET_ENABLE_PROFILING*" ".NET" "loads a profiler"
            vector "CORECLR_PROFILER*" ".NET" "the profiler to load"
            vector "CORECLR_ENABLE_PROFILING" ".NET" "loads a profiler"
            vector "COR_PROFILER*" ".NET Framework" "the profiler to load"
            vector "COR_ENABLE_PROFILING" ".NET Framework" "loads a profiler"
            vector "LD_PRELOAD" "Linux dynamic loader" "loads libraries into every program"
            vector "LD_AUDIT" "Linux dynamic loader" "loads auditing libraries into every program"
            vector "LD_LIBRARY_PATH" "Linux dynamic loader" "where libraries are looked up first"
            vector "DYLD_*" "macOS dynamic loader" "inserted libraries and library paths"
            vector "PYTHONSTARTUP" "Python" "a file run when the interpreter starts"
            vector "PERL5OPT" "Perl" "extra command-line options"
            vector "RUBYOPT" "Ruby" "extra command-line options"
            vector "JAVA_TOOL_OPTIONS" "Java" "extra JVM options, e.g. agents"
            vector "_JAVA_OPTIONS" "Java" "extra JVM options"
        ]

    /// Every pattern in `all`.
    let patterns: string list = all |> List.map (fun v -> v.Pattern)

    /// Whether a variable name is an injection vector (compared without case on every platform:
    /// denying `bun_options` on Linux costs nothing).
    let isVector (name: string) : bool =
        patterns |> List.exists (fun pattern -> EnvPattern.matches true pattern name)
