namespace Fx.Host

open Fx.Effects
open Fx.Models
open Fx.Store
open Fx.Components
open Fx.Variants
open Fx.Environments

/// The host's environment (spec §4.2: concrete environments are assembled from interpreters): every
/// capability the host's own machinery runs with. Sessions, grants, environments, the installer and
/// the extension bootstrap each take the part they declare. Built in stages by `Host.start`: the
/// variants, blobs, model and environments come once the stores and extensions are up.
type HostEnv =
    {
        Clock: Clock
        Ids: Ids
        Trace: Trace
        /// The host's resolution (host config, trusted project config, explicit flags, policy).
        Variants: Variants
        FsRead: FsRead
        Fs: Fs
        Shell: Shell
        Http: Http
        Fetch: Fetch
        InstallFiles: InstallFiles
        Blobs: Blobs
        /// Models, through `ModelAccess` only (spec §18).
        Llm: Llm
        Envs: Envs
        Scope: Scope
    }

    interface IClock with
        member env.Clock = env.Clock

    interface IIds with
        member env.Ids = env.Ids

    interface ITrace with
        member env.Trace = env.Trace

    interface IVariants with
        member env.Variants = env.Variants

    interface IFs with
        member env.FsRead = env.FsRead
        member env.Fs = env.Fs

    interface IShell with
        member env.Shell = env.Shell

    interface IHttp with
        member env.Http = env.Http

    interface IFetch with
        member env.Fetch = env.Fetch

    interface IInstallFiles with
        member env.InstallFiles = env.InstallFiles

    interface IBlobs with
        member env.Blobs = env.Blobs

    interface ILlm with
        member env.Llm = env.Llm

    interface IEnvs with
        member env.Envs = env.Envs

    interface IScopeWith<HostEnv> with
        member env.Scope = env.Scope
        member env.WithScope scope = { env with Scope = scope }

    override env.ToString() = $"HostEnv({env.Scope})"
