#!/usr/bin/env bash
# NativeAOT release gate (design/05-dotnet-support.md §5):
#   1. publish the real consumer host with PublishAot=true for each framework channel,
#   2. AFTER publishing, write brand-new JavaScript sources to a temp directory,
#   3. run the native executable directly against them (no `dotnet run`, no embedded scripts).
# Usage: eng/verify-aot.sh [rid]   (default: current machine RID)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RID="${1:-$(dotnet --info | awk '/ RID:/ {print $2; exit}')}"
OUT="$ROOT/artifacts/aot"
HOST="$ROOT/samples/Jaunt.AotHost/Jaunt.AotHost.csproj"
mkdir -p "$OUT"

publish() { # tfm workdir(for global.json selection)
  local tfm="$1" wd="$2"
  echo "=== publish $tfm ($RID) via SDK $(cd "$wd" && dotnet --version)"
  (cd "$wd" && dotnet publish "$HOST" -f "$tfm" -r "$RID" -c Release -o "$OUT/$tfm" -nologo -v:q) \
    | grep -Ei "warn|error|->" || true
}

publish net8.0 "$ROOT"
publish net10.0 "$ROOT"
if [ -f "$ROOT/eng/sdk11/global.json" ]; then publish net11.0 "$ROOT/eng/sdk11"; fi

# ---- scripts created only now, after the binaries exist
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
STAMP="$(date +%s%N)"
cat > "$WORK/script.js" <<EOF
// generated at $STAMP
var quantity = 20;
const subtotal = price * quantity;
subtotal + tax(subtotal) + 0 * $RANDOM
EOF
cat > "$WORK/language.js" <<'EOF'
const checks = [];
class Shape { #id; static count = 0; constructor(id) { this.#id = id; Shape.count++ } get id() { return this.#id } area() { return 0 } }
class Rect extends Shape { constructor(w, h) { super('r'); this.w = w; this.h = h } area() { return this.w * this.h + super.area() } }
checks.push(new Rect(2, 3).area() === 6 && Shape.count === 1 && new Rect(1, 1).id === 'r');
function* fib() { let [a, b] = [0, 1]; for (;;) { yield a; [a, b] = [b, a + b] } }
const f = []; for (const x of fib()) { if (x > 50) break; f.push(x) }
checks.push(f.join() === '0,1,1,2,3,5,8,13,21,34');
const { a, b: { c = 3 } = {}, ...rest } = { a: 1, d: 4, e: 5 };
checks.push(a + c === 4 && Object.keys(rest).join() === 'd,e');
checks.push('2026-11-06'.replace(/(\d+)-(\d+)-(\d+)/, '$3/$2/$1') === '06/11/2026');
checks.push(JSON.stringify(JSON.parse('{"x":[1,{"y":null}]}')) === '{"x":[1,{"y":null}]}');
const m = new Map([['k', 1]]); const s = new Set([1, 1, 2]);
checks.push(m.get('k') === 1 && s.size === 2);
const fns = []; for (let i = 0; i < 3; i++) fns.push(() => i);
checks.push(fns.map(g => g()).join() === '0,1,2');
let log = ''; try { try { throw new RangeError('x') } finally { log += 'f' } } catch (e) { log += e.name }
checks.push(log === 'fRangeError');
checks.push([3, 1, 2].sort((x, y) => x - y).join() === '1,2,3' && [1, [2, [3]]].flat(Infinity).length === 3);
checks.push(`${1 + 1}${'x'.repeat(2)}` === '2xx' && typeof Symbol.iterator === 'symbol');
checks.push(new Proxy({}, { get: (t, k) => k }).hello === 'hello' && Reflect.ownKeys({ q: 1 })[0] === 'q');
checks.push(new Date(Date.UTC(2020, 0, 2)).toISOString() === '2020-01-02T00:00:00.000Z');
checks.push(new Uint8Array([1, 2, 300])[2] === 44 && Number('0x10') === 16 && (0.1 + 0.2).toFixed(2) === '0.30');
checks.every(Boolean) ? 'ok' : 'failed: ' + checks.map((v, i) => v ? '' : i).filter(String).join()
EOF
cat > "$WORK/util.mjs" <<'EOF'
export const upper = s => s.toUpperCase();
export let calls = 0;
export function count() { return ++calls }
EOF
cat > "$WORK/plugin.mjs" <<'EOF'
import { upper, count } from 'util.mjs';
export function transform(order) {
  count();
  const total = order.lines.reduce((s, l) => s + l.qty * l.price, 0);
  return { id: order.id, items: order.lines.reduce((s, l) => s + l.qty, 0), total, names: order.lines.map(l => upper(l.name)).join() };
}
EOF
cat > "$WORK/loop.js" <<'EOF'
let i = 0; while (true) { i++ }
EOF
cat > "$WORK/async.js" <<'EOF'
(async () => {
  const parts = await Promise.all(['alpha', 'beta', 'gamma'].map(k => fetchValue(k)));
  const extra = await new Promise(r => r(1));
  return parts.reduce((a, b) => a + b, 0) - 1 + extra;
})()
EOF
cat > "$WORK/tla.mjs" <<'EOF'
const v = await Promise.resolve('tla');
export const value = v + ':' + (await Promise.all([1, 2, 3])).reduce((a, b) => a + b);
EOF
cat > "$WORK/stateful.mjs" <<'EOF'
let calls = 0;
export function handle(order) { calls++; globalThis.leak = (globalThis.leak ?? 0) + 1; return { id: order.id, items: order.lines.length, total: 0, names: 'calls=' + Math.max(calls, globalThis.leak) } }
EOF

status=0
ran=0
for tfm in net8.0 net10.0 net11.0; do
  bin="$OUT/$tfm/Jaunt.AotHost"
  [ -f "$bin.exe" ] && bin="$bin.exe"   # Windows RIDs
  [ -f "$bin" ] || continue
  ran=$((ran + 1))
  echo "=== run $tfm native binary ($(du -h "$bin" | cut -f1))"
  if ! "$bin" verify "$WORK"; then status=1; fi
done
# a gate that ran nothing must not pass (e.g. a publish failure or a missing toolchain)
if [ "$ran" -eq 0 ]; then echo "no native binary was produced"; exit 1; fi
if [ -n "${REQUIRE_TFMS:-}" ] && [ "$ran" -lt "$REQUIRE_TFMS" ]; then echo "only $ran of $REQUIRE_TFMS framework binaries ran"; exit 1; fi
exit $status
