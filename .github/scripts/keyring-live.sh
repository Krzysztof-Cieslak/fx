# The live keyring tests on Linux (CI's keyring steps): under one dbus-run-session with an unlocked
# gnome-keyring, the keyring's tests, the credential-store conformance suite on the keyring store
# (tests/Fx.Auth.Tests) and the keyring scenarios of the JIT-vs-native diff, with KEYRING_TESTS=1.
#
#   sh .github/scripts/keyring-live.sh <rid>      needs dbus, gnome-keyring, the built tests, the
#                                                 published scenarios and KEYRING_TEST_PASSWORD
# shellcheck disable=SC2016 # the session script expands its variables in its own shell
set -eu
XDG_RUNTIME_DIR="$(mktemp -d)"; export XDG_RUNTIME_DIR
exec dbus-run-session -- sh -c '
  set -eu
  printf %s "$KEYRING_TEST_PASSWORD" | gnome-keyring-daemon --unlock --components=secrets >/dev/null
  sh .github/scripts/keyring-wait.sh
  export KEYRING_TESTS=1
  dotnet test --project tests/Fx.Keyring.Tests --no-build --configuration Release
  dotnet test --project tests/Fx.Auth.Tests --no-build --configuration Release
  dotnet run --project tests/aot/Fx.Aot.Diff --configuration Release -- --rid "$0" --no-publish --only keyring-
' "$1"
