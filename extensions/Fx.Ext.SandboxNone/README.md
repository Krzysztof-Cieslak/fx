# fx.sandbox.none

The bundled sandbox driver that confines nothing (spec §9.1, §16.4): every process starts as it is,
with the user's full access, and only the type-level attenuation of the host's bindings applies (a
read-only workspace refuses `fs.write`, but a shell command can still write).

It is the default (T137: the sandbox is opt-in, `sandbox.driver = "none"`), which carries no
warning: clients show `sandbox: off`. It is also what `auto` falls back to on Windows in v1
(AppContainer comes with T511) and where bubblewrap or Seatbelt cannot run; then the environment
carries a persistent warning that says why (`Envs.warnings`; the TUI's banner, T130). A spec or a
project cannot ask for it while the host turns a confining driver on.

A native extension built on the public SDK only; the module spec is
[docs/modules/sandbox.md](../../docs/modules/sandbox.md).
