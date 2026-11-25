# fx.store.ephemeral

The bundled credential store in the host's memory (spec §11.4, §16.4): forgotten when the host stops,
never written anywhere. For CI and throwaway hosts, with credentials from the environment (the `env`
auth method and secret source): `credentials.store = ephemeral` writes nothing to disk. A native
extension built on the public SDK only; the module spec is
[docs/modules/auth.md](../../docs/modules/auth.md).

`auto` never picks it (it would lose what a login stored); choose it explicitly.
