#!/usr/bin/env bash
# Jaunt hot-path throughput: NativeAOT vs CoreCLR JIT. The same host (samples/Jaunt.AotHost, `bench` subcommand) runs the
# bench/cross/workloads corpus under the cross-engine protocol twice: once as the published native binary (engine jaunt-aot),
# once as the framework-dependent `dotnet Jaunt.AotHost.dll` (engine jaunt-jit). Results are checked against expected.json.
# usage: bench/cross/aot-vs-jit.sh [runner args, e.g. --filter apps/ --samples 3 --target-ms 60]
#   TFM=net8.0 / RID=linux-x64 override the framework and runtime identifier (default: net10.0, this machine).
#   METRIC=cpu makes the report compare CPU time per call instead of wall time (robust on a busy machine).
set -euo pipefail
cd "$(dirname "$0")/../.."
TFM="${TFM:-net10.0}"
RID="${RID:-$(dotnet --info | awk '/ RID:/ {print $2; exit}')}"
OUT=artifacts/cross; mkdir -p $OUT
HOST=samples/Jaunt.AotHost
echo "=== publish NativeAOT ($TFM, $RID)"
rm -rf "$OUT/aot-host"   # never run a stale binary from an earlier publish
dotnet publish $HOST -f $TFM -r $RID -c Release -o $OUT/aot-host -nologo -v q | grep -Ei "warn|error" || true
echo "=== build CoreCLR host ($TFM)"
dotnet build $HOST -f $TFM -c Release -nologo -v q | grep -Ei "warn|error" || true
BIN=$OUT/aot-host/Jaunt.AotHost; [ -f "$BIN.exe" ] && BIN="$BIN.exe"
# One after the other so they never compete for CPU.
echo "=== jaunt-aot: $BIN ($(du -h "$BIN" | cut -f1))"
"$BIN" bench bench/cross/workloads bench/cross/expected.json --json $OUT/jaunt-aot.jsonl "$@" || echo "(some workloads did not pass under AOT, see the table)"
echo "=== jaunt-jit: dotnet $HOST/bin/Release/$TFM/Jaunt.AotHost.dll"
dotnet $HOST/bin/Release/$TFM/Jaunt.AotHost.dll bench bench/cross/workloads bench/cross/expected.json --json $OUT/jaunt-jit.jsonl "$@" || echo "(some workloads did not pass under JIT, see the table)"
echo
python3 bench/cross/report_aot.py $OUT/aot-vs-jit.json $OUT/jaunt-jit.jsonl $OUT/jaunt-aot.jsonl
