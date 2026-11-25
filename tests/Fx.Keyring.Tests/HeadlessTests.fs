module Fx.Keyring.Tests.HeadlessTests

open Expecto
open Fx.Keyring

let private facts (os: HostOs) (variables: (string * string) list) : HostFacts =
    {
        Os = os
        Variables = Map.ofList variables
        RuntimeBusExists = false
        WslKernel = false
        ContainerMarker = false
    }

[<Tests>]
let tests =
    testList
        "Headless"
        [
            test "the session bus comes from DBUS_SESSION_BUS_ADDRESS, else $XDG_RUNTIME_DIR/bus if it exists" {
                let address = "unix:path=/tmp/dbus-abc,guid=0123"

                Expect.equal
                    (Headless.sessionBus (facts HostOs.Linux [ "DBUS_SESSION_BUS_ADDRESS", address ]))
                    (Some address)
                    "explicit"

                let runtime = facts HostOs.Linux [ "XDG_RUNTIME_DIR", "/run/user/1000" ]
                Expect.equal (Headless.sessionBus runtime) None "no bus socket"

                Expect.equal
                    (Headless.sessionBus { runtime with RuntimeBusExists = true })
                    (Some "unix:path=/run/user/1000/bus")
                    "systemd user bus"

                Expect.equal
                    (Headless.sessionBus
                        { facts HostOs.Linux [ "XDG_RUNTIME_DIR", "/run/user/a b"; "DBUS_SESSION_BUS_ADDRESS", "" ] with
                            RuntimeBusExists = true
                        })
                    (Some "unix:path=/run/user/a%20b/bus")
                    "escaped, and an empty address is unset"

                Expect.equal (Headless.sessionBus (facts HostOs.Linux [])) None "containers and CI: none"
            }

            test "the session says SSH, display, WSL, container and CI" {
                let s =
                    Headless.session (
                        facts
                            HostOs.Linux
                            [
                                "SSH_CONNECTION", "10.0.0.1 22 10.0.0.2 50000"
                                "WAYLAND_DISPLAY", "wayland-0"
                                "WSL_DISTRO_NAME", "Ubuntu"
                                "container", "podman"
                                "CI", "true"
                            ]
                    )

                Expect.equal (string s) "ssh,display,wsl,container,ci" "everything"
                Expect.equal (string (Headless.session (facts HostOs.Linux []))) "local" "nothing"

                Expect.isTrue
                    (Headless.session
                        { facts HostOs.Linux [] with
                            WslKernel = true
                        })
                        .Wsl
                    "the kernel tells WSL"

                Expect.isTrue
                    (Headless.session
                        { facts HostOs.Linux [] with
                            ContainerMarker = true
                        })
                        .Container
                    "a marker file tells a container"

                Expect.isFalse
                    (Headless.session (facts HostOs.Windows [ "WSL_DISTRO_NAME", "x" ])).Wsl
                    "WSL is Linux only"
            }

            test "prompts only where someone can answer them" {
                let prompts os variables = Headless.prompts (facts os variables)
                Expect.equal (prompts HostOs.Linux [ "DISPLAY", ":0" ]) Prompts.Allow "desktop Linux"
                Expect.equal (prompts HostOs.Linux []) Prompts.Never "Linux without a display"

                Expect.equal
                    (prompts HostOs.Linux [ "DISPLAY", ":0"; "SSH_TTY", "/dev/pts/0" ])
                    Prompts.Never
                    "X forwarding over SSH"

                Expect.equal (prompts HostOs.MacOS []) Prompts.Allow "macOS console"
                Expect.equal (prompts HostOs.MacOS [ "SSH_CONNECTION", "x" ]) Prompts.Never "macOS over SSH"
                Expect.equal (prompts HostOs.Windows [ "CI", "true" ]) Prompts.Never "CI"
                Expect.equal (prompts HostOs.Windows []) Prompts.Allow "Windows desktop"
            }

            test "local settings follow the facts" {
                let settings =
                    LocalKeyring.settings (
                        facts HostOs.Linux [ "DBUS_SESSION_BUS_ADDRESS", "unix:abstract=/tmp/x"; "CI", "1" ]
                    )

                Expect.equal settings.SessionBus (Some "unix:abstract=/tmp/x") "bus"
                Expect.equal settings.Prompts Prompts.Never "no prompts in CI"
                Expect.isTrue settings.AutoStart "activation"
            }
        ]
