#!/usr/bin/env bash
# Smoke tests of the Native AOT binary of src/Fx.Cli on the target it was published for: CI's os and
# targets jobs run one check per step after `dotnet publish src/Fx.Cli` and
# `dotnet publish tests/aot/Fx.Aot.Scenarios` with `--runtime <rid>`.
#
#   .github/scripts/aot-smoke.sh <check> <rid>
#
#   binary   it runs, and prints its version and help; writes `name=<binary name>` to $GITHUB_OUTPUT
#   envd     envd mode answers `initialize` on standard input and output
#   host     the embedded host starts: `variants list`, and a headless run
#   sqlite   SQLite is linked into the binary
#   sizes    the binaries' sizes, in the job summary
#
# IN: the prefix that runs a command where the binary runs (`docker exec alpine` in the musl targets'
# Alpine container), or empty.
set -euo pipefail

check=${1:?usage: aot-smoke.sh <binary|envd|host|sqlite|sizes> <rid>}
rid=${2:?usage: aot-smoke.sh <check> <rid>}
IN=${IN:-}
exe=""
[[ "$rid" == win-* ]] && exe=".exe"
name=$($IN dotnet msbuild src/Fx.Cli -getProperty:AssemblyName | tr -d '\r')
dir="artifacts/publish/Fx.Cli/release_$rid"
bin="$dir/$name"

case "$check" in
  binary)
    [[ -n "${GITHUB_OUTPUT:-}" ]] && echo "name=$name" >> "$GITHUB_OUTPUT"
    ls -l "$dir"
    version=$($IN "$bin" --version | tr -d '\r')
    echo "$version"
    [[ "$version" == "$name "* ]]
    $IN "$bin" --help
    ;;

  envd)
    work="artifacts/envd-smoke"
    rm -rf "$work" && mkdir -p "$work/workspace" "$work/state"
    request='{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersions":["1"],"peer":{"name":"ci","version":"0"},"capabilities":{"maxMessageSize":1048576},"environmentProtocol":"1"}}'
    # Standard input must reach the container: `docker exec -i`.
    run=""
    [[ -n "$IN" ]] && run="docker exec -i alpine"
    reply=$(echo "$request" | $run "$bin" envd --stdio --workspace "$work/workspace" --state "$work/state" | tr -d '\r')
    echo "$reply"
    grep -q '"environmentProtocol":"1"' <<< "$reply"
    grep -q '"platform":{' <<< "$reply"
    ;;

  host)
    bin="$PWD/$bin"
    work="$PWD/artifacts/host-smoke"
    rm -rf "$work" && mkdir -p "$work/home" "$work/project"
    home="$work/home"
    [[ "$rid" == win-* ]] && home="$(cygpath -w "$home")"
    # The product's home variable (FX_HOME for the product name fx).
    variable="$(tr '[:lower:]' '[:upper:]' <<< "$name")_HOME"
    if [[ -n "$IN" ]]; then
      run=(docker exec -i -w "$work/project" -e "$variable=$home" alpine)
    else
      run=(env "$variable=$home")
    fi
    cd "$work/project"
    "${run[@]}" "$bin" variants list > "$work/list.txt"
    grep -q '^agent.interpreter = "bun-ts"' "$work/list.txt"
    # A headless run starts the whole host (stores, grants, identity, an embedded environment);
    # without a terminal the first install of Bun is denied, so the run stops there.
    set +e
    "${run[@]}" "$bin" run -p hello < /dev/null > "$work/run.txt" 2>&1
    code=$?
    set -e
    cat "$work/run.txt"
    [[ "$code" == 1 ]]
    grep -q "needs a person's approval" "$work/run.txt"
    ;;

  sqlite)
    # shellcheck disable=SC2010 # the publish directory's names are plain
    if ls "$dir" | grep -iE 'sqlite.*\.(so|dylib|dll|a|lib)$'; then
      echo "::error::a SQLite library was published next to the binary"
      exit 1
    fi
    # The binary alone in an empty directory: the library name is unique to the binding, so
    # nothing else could satisfy its imports.
    alone="artifacts/standalone/$rid"
    rm -rf "$alone" && mkdir -p "$alone" && cp "$bin$exe" "$alone/"
    info=$($IN "$alone/$name" --build-info | tr -d '\r')
    echo "$info"
    version=$($IN dotnet msbuild src/Fx.Sqlite -getProperty:FxSqliteVersion | tr -d '\r')
    grep -q "^sqlite $version " <<< "$info"
    ;;

  sizes)
    {
      echo "### Native AOT binaries, $rid"
      echo "| Binary | Bytes |"
      echo "|---|---:|"
      for file in "Fx.Cli/release_$rid/$name" "Fx.Aot.Scenarios/release_$rid/Fx.Aot.Scenarios"; do
        echo "| $(basename "$file") | $(wc -c < "artifacts/publish/$file$exe" | tr -d ' ') |"
      done
    } | tee -a "${GITHUB_STEP_SUMMARY:-/dev/null}"
    ;;

  *)
    echo "unknown check: $check" >&2
    exit 2
    ;;
esac
