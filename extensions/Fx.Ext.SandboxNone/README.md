# fx.sandbox.none

The bundled sandbox driver that confines nothing (spec §9.1, §16.4): every process starts as it is,
with the user's full access. Environments that use it carry a persistent warning that they run
unsandboxed (`Envs.warnings`; the TUI shows it, T130), and only the type-level attenuation of the
host's bindings applies (a read-only workspace refuses `fs.write`, but a shell command can still
write).

It is what `auto` picks on Windows in v1 (AppContainer comes with T511), and where bubblewrap or
Seatbelt cannot run (the warning says why). A host turns sandboxing off with the privileged variant
`sandbox.driver = "none"`; a spec or a project cannot ask for it while a confining driver is in
effect.

A native extension built on the public SDK only; the module spec is
[docs/modules/sandbox.md](../../docs/modules/sandbox.md).
