# fx.sandbox.seatbelt

The bundled macOS sandbox driver (spec §9.1, §16.4): every process an agent starts in an environment
(commands, terminals, kernels, setup) runs under `/usr/bin/sandbox-exec` with a Seatbelt profile
built from the environment's policy. A native extension built on the public SDK only; the module
spec is [docs/modules/sandbox.md](../../docs/modules/sandbox.md).

## The profile (`Profile.fs`)

`/usr/bin/sandbox-exec -p <profile> -DP0=<path> … -- <program>`: paths are parameters
(`(param "P0")`), never part of the profile's text, so nothing needs quoting.

1. `(deny default)`, then what every process needs: starting programs, signals and process
   information within its own sandbox, a list of system information (`sysctl`) reads, user lookups,
   POSIX semaphores, terminals (`/dev/ptmx`, `/dev/ttys*`, `/dev/tty`) and the devices everyone writes
   (`/dev/null`, `/dev/stdout`, …).
2. **Reads**: every path but the denied ones; readable and writable paths inside denied ones again,
   and the metadata (not the content) of the denied directories above them, so that programs that
   look at each directory on the way (`mkdir -p`, `realpath`) reach them, as under bubblewrap.
   **Writes**: the writable roots (the workspace unless read-only, the launches' owned directories,
   the environment's temporary directory, which is also `TMPDIR`). Each root's region is its subpath
   minus the roots inside it, so the longest root decides, as `SandboxPolicy.access` says.
3. **Network**: nothing when denied (unix sockets included); when allowed, sockets, the services that
   resolve names and check certificates, and the routing table, but no unix socket under a denied
   path or in `/private/tmp` (the user's SSH agent), except, last, in the environment's own writable
   directories and `TMPDIR` (inside the denied data directory): .NET's named pipes, which MSBuild's
   worker nodes talk over, are there. With the network denied no unix socket connects, those
   included, so MSBuild's nodes and the compiler server cannot start.
4. Last, renaming or deleting the directories between a writable root and a protected path, or the
   protected path, is refused: Seatbelt matches paths, and a rename would carry what is inside past
   its rules.

Seatbelt matches real paths: a rule names a path as given, its real path from the probe
(`readlink -f` of every path of the policy, once per connection), and both below `/private` for
`/tmp`, `/var` and `/etc`.

## The probe

macOS only; `/usr/bin/sandbox-exec` must exist (it is never looked up on `PATH`), and a profile of
this driver must run `/usr/bin/true`. It reports the macOS version (`sw_vers`) and the real paths of
the environment's paths.

## Risks and limits

- **`sandbox-exec` is deprecated** (its man page has said so since macOS 10.12) and SBPL is not
  documented. It is still present on every macOS release and is what other coding agents and
  Chromium's tests use; if Apple removes it, the probe fails, `auto` falls back to `none` with a
  persistent warning, and a replacement (an App Sandbox helper, or containers) is a new driver.
- The user's home is read-only (see the bubblewrap driver's README for caches and
  `sandbox.writable`).
- Unlike bubblewrap there is no new process namespace: a sandboxed process can see the user's other
  processes (not their memory), and processes it starts are not stopped when envd is killed.
