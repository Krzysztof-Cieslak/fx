# fx.sandbox.bubblewrap

The bundled Linux sandbox driver (spec §9.1, §16.4): every process an agent starts in an environment
(commands, terminals, kernels, setup) runs inside [bubblewrap](https://github.com/containers/bubblewrap)
with a seccomp filter. Opt-in (T137): a host turns it on with the privileged variant
`sandbox.driver = "auto"` (or `"bubblewrap"` to require it); by default no sandbox applies. A native
extension built on the public SDK only; the module spec is
[docs/modules/sandbox.md](../../docs/modules/sandbox.md), whose "Known limits" says what stops working
in it.

## What a process gets

`bwrap --die-with-parent --unshare-all [--share-net] [--unshare-user --disable-userns] [--new-session]
--ro-bind / / --dev /dev --proc /proc --tmpfs /tmp <the policy's paths> [--chdir <cwd>] -- <program>`

- **Namespaces**: user, pid, ipc, uts and cgroup always; the network too unless the policy allows it
  (then only loopback exists inside). `--disable-userns` (bwrap ≥ 0.8) stops nested user namespaces.
- **Files**: the whole system read-only; then the policy's paths, shortest first so that longer ones
  go over them: denied directories become empty tmpfs mounts (remounted read-only), denied files
  `/dev/null`; readable and protected paths read-only binds; writable ones (the workspace, the
  launches' owned directories, the environment's temporary directory) read-write binds. The
  directories between a writable root and a read-only path inside it are bound onto themselves:
  mount points cannot be renamed, so `mv .git x` cannot carry `.git/hooks` away.
- **`/tmp`** is private and empty for each process; `TMPDIR` is the environment's own temporary
  directory (`<home>/tmp`), which persists between commands.
- **Pipes** get a new session (no controlling terminal to type into); **terminals** keep theirs.
- **Lifetime**: bwrap and everything it started die with envd (`--die-with-parent`, and the pid
  namespace ends with its first process). `--die-with-parent` is `PR_SET_PDEATHSIG`, which fires when
  the *thread* that started bwrap ends: envd starts processes on a thread that lives as long as it does
  (docs/modules/envd.md, T135).

Paths that do not exist when a process starts are left out. A denied or protected path that is a
symbolic link the probe did not resolve refuses the start (a mount over it would follow it); the
probe resolves every path of the environment's policy once per connection (`readlink -f`).

## The seccomp filter (`Seccomp.fs`)

A classic BPF program built as data for x86-64 and arm64, written by the probe to
`<environment home>/sandbox/seccomp-<arch>.bpf` and handed to bwrap on descriptor 3
(`/bin/sh -c 'exec "$@" 3< "$0"' <file> bwrap --seccomp 3 …`: processes start with no other
descriptors). It:

- refuses with `EPERM`: `ptrace`, `process_vm_readv`/`writev`, kernel modules and `kexec`, every mount
  call (old and new API), `pivot_root`, `chroot`, `setns`, swap, reboot, the clocks, `acct`, the
  kernel keyring (`add_key`, `request_key`, `keyctl`), `bpf`, `perf_event_open`, `userfaultfd`, file
  handles, `quotactl`, `lookup_dcookie`, `syslog`, and on x86-64 `iopl`, `ioperm`, `uselib`;
- refuses `clone3` with `ENOSYS` (its flags are in memory, which BPF cannot read; libc falls back to
  `clone`), and `clone`/`unshare` with `CLONE_NEWUSER`;
- refuses `ioctl` `TIOCSTI` and `TIOCLINUX` (typing into a terminal);
- kills a process that makes another architecture's or the x32 ABI's system calls.

Its tests run it through a BPF interpreter (`Seccomp.evaluate`) as the kernel would.

## Requirements and the probe

The probe (once per connection to envd) finds `bwrap` on `PATH`, reads its version and the kernel's
user-namespace settings, tries a sandbox, then `--disable-userns` and the seccomp filter, keeping each
only if the trial still passes (a note says what is missing). When bwrap cannot run, the probe says
why and what to do; `auto` then falls back to `none` with a persistent warning, and a spec or host
that requires bubblewrap fails the environment.

- **Install it**: `apt install bubblewrap`, `dnf install bubblewrap`, `apk add bubblewrap`, `pacman -S
  bubblewrap`.
- **Ubuntu 23.10 and later** restrict unprivileged user namespaces with AppArmor
  (`kernel.apparmor_restrict_unprivileged_userns = 1`): bwrap fails with errors such as
  `loopback: Failed RTM_NEWADDR: Operation not permitted`. Let bwrap create them with a profile:

  ```
  # /etc/apparmor.d/bwrap
  abi <abi/4.0>,
  include <tunables/global>
  profile bwrap /usr/bin/bwrap flags=(unconfined) {
    userns,
    include if exists <local/bwrap>
  }
  ```

  then `sudo apparmor_parser -r /etc/apparmor.d/bwrap`; or turn the restriction off
  (`sudo sysctl -w kernel.apparmor_restrict_unprivileged_userns=0`, which CI does).
- **Older Debian kernels**: `sudo sysctl -w kernel.unprivileged_userns_clone=1`.
- **Containers** usually forbid user namespaces: run the container with
  `--security-opt seccomp=unconfined --security-opt apparmor=unconfined`, or leave the privileged
  variant `sandbox.driver` at its default, `none` (and rely on the container).

## Limits

- The user's home is read-only: tools that write caches there (`~/.cache`, `~/.nuget/packages`,
  `~/.bun`) fail unless the host adds them with the privileged variant `sandbox.writable`, and each
  writable cache is a way for what an agent wrote to run outside the sandbox later.
- A denied path created after a process started is visible to that process; one created after the
  environment was opened is hidden from the next process only if it is not a symbolic link.
- The filter is a deny list: new kernel calls are allowed until they are added here.
