namespace Fx.Keyring

open Fx.Keyring.SecretService

/// How the host opens this machine's keyring.
type LocalKeyringSettings =
    {
        Prompts: Prompts
        /// Linux: the session bus (`Headless.sessionBus`); `None` when there is none.
        SessionBus: string option
        /// Linux: whether calls may start the Secret Service by D-Bus activation.
        AutoStart: bool
    }

    override this.ToString() =
        $"LocalKeyringSettings(prompts {this.Prompts}, auto-start {this.AutoStart})"

/// The keyring of this machine: the Keychain on macOS, the Credential Manager on Windows, the Secret
/// Service on Linux. The interpreter of `IKeyring`; only composition roots use it.
module LocalKeyring =
    /// The settings that fit a machine: prompts only where someone can answer them, the session bus
    /// if there is one, and D-Bus activation.
    let settings (facts: HostFacts) : LocalKeyringSettings =
        {
            Prompts = Headless.prompts facts
            SessionBus = Headless.sessionBus facts
            AutoStart = true
        }

    let create (os: HostOs) (settings: LocalKeyringSettings) : Keyring =
        match os with
        | HostOs.Windows -> WindowsCredentials.create ()
        | HostOs.MacOS -> MacKeychain.create settings.Prompts
        | HostOs.Linux ->
            SecretServiceKeyring.create
                { SecretServiceKeyring.defaults settings.SessionBus settings.Prompts with
                    AutoStart = settings.AutoStart
                }
        | HostOs.Other name -> Keyring.unavailable "none" (Unavailable.NotSupported name)
