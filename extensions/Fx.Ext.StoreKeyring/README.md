# fx.store.keyring

The bundled credential store in the OS keyring (spec §11.4, §16.4): the macOS Keychain, the Windows
Credential Manager, or the Secret Service on Linux (gnome-keyring, KWallet, KeePassXC), through the
host's `IKeyring` ([docs/spikes/keychains.md](../../docs/spikes/keychains.md)). A native extension built
on the public SDK only; the module spec is [docs/modules/auth.md](../../docs/modules/auth.md).

- **One item per credential:** the service is the product's name, the account the credential's id
  (`fx` / `anthropic`), the value the credential's canonical JSON (`{"meta", "method", "secret"}`).
  People find every credential under one name in Keychain Access, Seahorse and the Credential Manager,
  and `secret-tool lookup service fx account anthropic` reads one.
- **Values stored by hand are plain secrets:** a value that is not a credential's JSON (one stored
  with `secret-tool store service fx account github-token`, Keychain Access or `cmdkey`) reads as a
  plain secret, so `secret:github-token` resolves to it.
- **What the keyring cannot do becomes what to do:** each unavailable state (no session bus, no
  Secret Service, no default keyring, no keychain, a Windows logon without credentials) is a reason
  that says how to fix it or which `credentials.store` to use instead; a locked keyring says how to
  unlock it. The host never falls back silently ([auth.md](../../docs/modules/auth.md#choosing-the-store)).
- **Limits:** the Credential Manager keeps at most 2560 bytes per value (API keys fit; larger token
  sets are `tooLarge`), and compares ids without case (ids are lowercase everywhere).
