# fx.store.file

The bundled credential store in one file only the user can read (spec §11.4, §16.4): the store for
machines without a usable keyring (containers, servers, SSH sessions, WSL;
[docs/spikes/keychains.md](../../docs/spikes/keychains.md)). A native extension built on the public SDK
only; the module spec is [docs/modules/auth.md](../../docs/modules/auth.md).

- **The file** is `credentials.json` among the host's private files (`IPrivateFiles`, in
  `<data dir>/credentials`): canonical JSON, `{"credentials": {"<id>": <StoredCredential>, …},
  "version": 1}`.
- **Protection** comes from the private files: on Unix the directory is `0700` and the file `0600`
  from its creation (as `gh`, `aws` and `gcloud` keep theirs); on Windows the file has an ACL with only
  the user and its content is encrypted with the user's DPAPI key (the product's entropy), or the ACL
  alone in a logon session without DPAPI keys (`Protection` says which).
- **Writes are whole:** each write or delete reads the file, changes it and writes it back atomically
  (staged and renamed), one at a time; one host writes a data directory at a time.
- **A damaged file is never overwritten:** reading it is `corrupt` (naming the file), the probe says
  the store cannot be used, and writes refuse until a person fixes or removes it.
- **Limit:** 64 KiB per credential's JSON (`tooLarge` beyond).
