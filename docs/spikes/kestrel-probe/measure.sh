#!/usr/bin/env bash
# Publishes every probe variant with Native AOT and prints a Markdown table of binary sizes and the
# median timings of several runs. Used for docs/spikes/kestrel.md; CI runs it on workflow_dispatch.
#
#   docs/spikes/kestrel-probe/measure.sh <rid>          e.g. linux-x64
#   IN="docker exec alpine" measure.sh linux-musl-x64   run every command in a container
#   RUNS=21 measure.sh <rid>                            runs per variant (default 11)
set -euo pipefail

rid=${1:?usage: measure.sh <rid>}
here=$(cd "$(dirname "$0")" && pwd)
IN=${IN:-}
runs=${RUNS:-11}
exe=""
[[ "$rid" == win-* ]] && exe=".exe"
variants=(console client server server-ws server-ws-routing slim-ws-routing)

median() { sort -n | awk '{ v[NR] = $1 } END { if (NR == 0) print "-"; else print v[int((NR + 1) / 2)] }'; }
mib() { awk -v b="$1" 'BEGIN { printf "%.2f", b / 1048576 }'; }

echo "| Variant | Size (bytes) | Size (MiB) | vs console (MiB) | main to listening (ms) | main to first response (ms) | main to exit (ms) |"
echo "|---|---:|---:|---:|---:|---:|---:|"

base=""
for variant in "${variants[@]}"; do
  $IN dotnet publish "$here/Probe.fsproj" -c Release -r "$rid" -p:Variant="$variant" -nologo -v:q >&2
  bin="$here/bin/$variant/publish/probe$exe"
  size=$(wc -c < "$bin" | tr -d ' ')
  [[ -z "$base" ]] && base=$size

  out=$(for _ in $(seq "$runs"); do $IN "$bin" | tr -d '\r'; done)
  listening=$(grep -o 'listening [0-9.]*' <<< "$out" | awk '{ print $2 }' | median || true)
  first=$(grep -o 'first response [0-9.]*' <<< "$out" | awk '{ print $3 }' | median || true)
  total=$(grep -o 'total [0-9.]*' <<< "$out" | awk '{ print $2 }' | median)
  echo "| $variant | $size | $(mib "$size") | +$(mib $((size - base))) | $listening | $first | $total |"
done
