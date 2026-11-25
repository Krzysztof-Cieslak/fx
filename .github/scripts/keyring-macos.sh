#!/usr/bin/env bash
# The live keyring on macOS (CI's keyring steps), in a temporary keychain with a throwaway password
# (KEYRING_TEST_PASSWORD), so nothing is ever written to the runner's own keychains.
#
#   .github/scripts/keyring-macos.sh create <rid>   creates it, removes its auto-lock, puts it first in
#                                                    the search list and makes it the default; exports
#                                                    KEYRING_TEST_KEYCHAIN through $GITHUB_ENV
#   .github/scripts/keyring-macos.sh live <rid>     the keyring's tests, the credential-store conformance
#                                                    suite on the keyring store and the keyring scenarios
#                                                    of the JIT-vs-native diff, with KEYRING_TESTS=1
#   .github/scripts/keyring-macos.sh detect <rid>   the probe, natively: ready, then locked
#   .github/scripts/keyring-macos.sh delete         restores the login keychain as the default and
#                                                    deletes the temporary one (run it always)
set -euo pipefail

command=${1:?usage: keyring-macos.sh <create|live|detect|delete> [rid]}
bin="artifacts/publish/Fx.Aot.Scenarios/release_${2:-}/Fx.Aot.Scenarios"

case "$command" in
  create)
    # The runners' own login keychain is unlocked: the probe says `ready` before the temporary
    # keychain exists.
    echo "The runner's own default keychain: $(security default-keychain -d user)"
    "$bin" run keyring-detect
    keychain="$RUNNER_TEMP/keyring-ci.keychain-db"
    security create-keychain -p "$KEYRING_TEST_PASSWORD" "$keychain"
    security set-keychain-settings "$keychain"
    security unlock-keychain -p "$KEYRING_TEST_PASSWORD" "$keychain"
    # shellcheck disable=SC2046 # one argument per keychain already in the list
    security list-keychains -d user -s "$keychain" $(security list-keychains -d user | tr -d '"')
    security default-keychain -d user -s "$keychain"
    echo "KEYRING_TEST_KEYCHAIN=$keychain" >> "$GITHUB_ENV"
    ;;

  live)
    export KEYRING_TESTS=1
    dotnet test --project tests/Fx.Keyring.Tests --no-build --configuration Release
    dotnet test --project tests/Fx.Auth.Tests --no-build --configuration Release
    dotnet run --project tests/aot/Fx.Aot.Diff --configuration Release -- --rid "$2" --no-publish --only keyring-
    ;;

  detect)
    KEYRING_EXPECT=ready "$bin" run keyring-detect
    security lock-keychain "$KEYRING_TEST_KEYCHAIN"
    KEYRING_EXPECT=locked "$bin" run keyring-detect
    security unlock-keychain -p "$KEYRING_TEST_PASSWORD" "$KEYRING_TEST_KEYCHAIN"
    ;;

  delete)
    security default-keychain -d user -s login.keychain-db || true
    security delete-keychain "$KEYRING_TEST_KEYCHAIN" || true
    ;;

  *)
    echo "unknown command: $command" >&2
    exit 2
    ;;
esac
