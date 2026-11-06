#!/usr/bin/env bash
# Cross-engine benchmark: Jaunt vs Jint (.NET) vs moejs (Go) on bench/cross/workloads.
# usage: bench/cross/run.sh [extra runner args, e.g. --filter apps/ --samples 3]
set -euo pipefail
cd "$(dirname "$0")/../.."
OUT=artifacts/cross; mkdir -p $OUT
node bench/cross/tools/node-expected.mjs bench/cross/workloads bench/cross/expected.json >/dev/null
dotnet build -c Release bench/Jaunt.Benchmarks -v q --nologo | tail -1
(cd bench/cross/moejs-runner && go build -o ../../../$OUT/moejs-runner .)
# Engines run one after another so they never compete for CPU.
dotnet bench/Jaunt.Benchmarks/bin/Release/net10.0/Jaunt.Benchmarks.dll --cross bench/cross/workloads bench/cross/expected.json --json $OUT/dotnet.jsonl "$@"
GO_ARGS=(); while [ $# -gt 0 ]; do case "$1" in --filter) GO_ARGS+=(-filter "$2"); shift 2;; --samples) GO_ARGS+=(-samples "$2"); shift 2;; --target-ms) GO_ARGS+=(-target-ms "$2"); shift 2;; --warm-ms) GO_ARGS+=(-warm-ms "$2"); shift 2;; *) shift;; esac; done
$OUT/moejs-runner -dir bench/cross/workloads -expected bench/cross/expected.json -json $OUT/moejs.jsonl ${GO_ARGS[@]+"${GO_ARGS[@]}"}
python3 bench/cross/report.py $OUT/summary.json $OUT/dotnet.jsonl $OUT/moejs.jsonl
