# The keyring probe's headless states on Linux, natively (CI's keyring steps): the published
# scenarios binary must report each state in turn. The probe never starts a service (no D-Bus
# activation), so each state below is what the machine is.
#
#   sh .github/scripts/keyring-detect.sh <rid>     needs dbus and gnome-keyring, and
#                                                  KEYRING_TEST_PASSWORD (a throwaway password)
#
# Run it before keyring-live.sh: once `--unlock` has created a login keyring, `--start` no longer
# gives the no-default-collection state. Each session gets its own XDG_RUNTIME_DIR, so no other
# daemon's control socket (or systemd's socket unit) is found.
# shellcheck disable=SC2016 # the session scripts expand their variables in their own shell
set -eu
bin="artifacts/publish/Fx.Aot.Scenarios/release_$1/Fx.Aot.Scenarios"
env -u DBUS_SESSION_BUS_ADDRESS -u XDG_RUNTIME_DIR KEYRING_EXPECT="unavailable: no-session-bus" "$bin" run keyring-detect
XDG_RUNTIME_DIR="$(mktemp -d)"; export XDG_RUNTIME_DIR
dbus-run-session -- env KEYRING_EXPECT="unavailable: no-secret-service" "$bin" run keyring-detect
XDG_RUNTIME_DIR="$(mktemp -d)"; export XDG_RUNTIME_DIR
dbus-run-session -- sh -c '
  set -eu
  gnome-keyring-daemon --start --components=secrets >/dev/null
  sh .github/scripts/keyring-wait.sh
  KEYRING_EXPECT="unavailable: no-default-collection" "$0" run keyring-detect
' "$bin"
XDG_RUNTIME_DIR="$(mktemp -d)"; export XDG_RUNTIME_DIR
dbus-run-session -- sh -c '
  set -eu
  printf %s "$KEYRING_TEST_PASSWORD" | gnome-keyring-daemon --unlock --components=secrets >/dev/null
  sh .github/scripts/keyring-wait.sh
  KEYRING_EXPECT=ready "$0" run keyring-detect
  dbus-send --session --print-reply --dest=org.freedesktop.secrets /org/freedesktop/secrets \
    org.freedesktop.Secret.Service.Lock array:objpath:/org/freedesktop/secrets/collection/login >/dev/null
  KEYRING_EXPECT=locked "$0" run keyring-detect
' "$bin"
