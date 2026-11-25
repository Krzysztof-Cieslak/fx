# fx.env.local

The bundled environment provider for the host's own machine (spec §3 embedded mode, §9): the
workspace is an existing directory used in place, and envd runs in the host's process (transport
`in-process`, the default) or as the host's binary in a child process (`piped`). Module spec:
[docs/modules/environments.md](../../docs/modules/environments.md).

A native extension: its manifest declares one contribution, the provider `local`
(`EnvProviders.contributionType`, privileged), and its activation provides `EnvLocal.provider`. Like
every package here it references `src/Fx.Sdk` and nothing else: it reaches the machine through the
`IProviderEnv` the host hands it (files, processes, the data directory), never through core code.

| File | Contents |
|---|---|
| `EnvLocal.fs` | The provider (caps, `config`, `Create`, `Platform`, `Exec`, `Upload`, `Destroy`, `List`), the manifest and the extension |

```json
{ "environments": { "dev": { "provider": "local", "workspace": { "path": "~/src/app" } } } }
```

- Each environment has a home, `<data dir>/environments/<id>` (`~/.fx/…`, `%LOCALAPPDATA%\fx\…`),
  holding `lease.json` and envd's state (`envd/`: blobs, spilled output, the shadow repository).
  Destroying an environment deletes its home and never the workspace.
- `config` takes no settings (`{}`); `sandbox` must be `none` until T113 brings drivers (every
  environment carries the unsandboxed warning); `network` must be `open`.
- The host scrubs its own variables (credentials, injection vectors) before envd sees them.

Tests: the provider conformance suite in process and piped, and the local specifics
(`tests/Fx.Environments.Tests/LocalTests.fs`); loading as a built-in and refusing a project's copy
(`tests/Fx.Extensions.Tests/BootstrapTests.fs`).
