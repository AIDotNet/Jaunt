using System.Linq;
using Jaunt;
using Xunit;

namespace Jaunt.Tests;

/// <summary>
/// Every array storage mode that keeps <c>Items</c> empty (queue and page mode, entered by shift, push, filter, flat, flatMap
/// and Array.from) through every way of reading and changing an array. The expected values were produced by Node.
/// </summary>
public class ArrayStorageModeTests
{
    private const string Script = """"
const N = 5000;
const h = (a) => { let s = 0; for (let i = 0; i < a.length; i++) { const v = a[i]; s = (s * 31 + (v === undefined ? 7 : typeof v === 'number' ? v : String(v).length)) | 0; } return a.length + ':' + s; };
const seq = (n) => Array.from({ length: n }, (_, i) => i);
const modes = {
  pushed: () => { const a = []; for (let i = 0; i < N; i++) a.push(i); return a; },
  pushedBig: () => { const a = []; for (let i = 0; i < 20000; i++) a.push(i); return a; },
  filtered: () => seq(N + 100).filter((x) => x < N),
  flatted: () => seq(N).map((x) => [x]).flat(),
  flatMapped: () => seq(N).flatMap((x) => [x]),
  arrayFrom: () => Array.from(new Set(seq(N))),
  shifted: () => { const a = seq(N + 3); a.shift(); a.shift(); a.shift(); return a.map((x) => x - 3); },
  pushedShifted: () => { const a = []; for (let i = 0; i < N + 1; i++) a.push(i - 1); a.shift(); return a; },
};
const ops = {
  index: (a) => a[0] + a[N - 1] + String(a[N]),
  write: (a) => { a[10] = -1; a[N - 1] = -2; return h(a); },
  writePast: (a) => { a[N + 5] = 1; return a.length + ':' + (5 in a) + ':' + ((N + 2) in a) + ':' + a[N + 2]; },
  lengthCut: (a) => { a.length = 100; return h(a) + ':' + a[100]; },
  lengthGrow: (a) => { a.length = N + 10; return a.length + ':' + ((N + 3) in a); },
  deleteOne: (a) => { delete a[3]; return (3 in a) + ':' + a[3] + ':' + a.indexOf(4) + ':' + Object.keys(a).length; },
  keys: (a) => { const k = Object.keys(a); return k.length + ':' + k[0] + ':' + k[k.length - 1]; },
  own: (a) => Reflect.ownKeys(a).length + ':' + a.hasOwnProperty(N - 1) + ':' + a.hasOwnProperty(N) + ':' + (Object.getOwnPropertyDescriptor(a, 7) || {}).value,
  entries: (a) => Object.entries(a).length + ':' + Object.values(a)[5],
  forIn: (a) => { let c = 0; for (const k in a) c += +k; return c; },
  forOf: (a) => { let c = 0; for (const v of a) c = (c + v) | 0; return c; },
  spreadLit: (a) => h([...a, 1]),
  spreadCall: (a) => Math.max(...a) + ':' + ((...r) => r.length)(...a),
  apply: (a) => Math.min.apply(null, a) + ':' + Reflect.apply((...r) => r[N - 1], null, a) + ':' + ((x, y) => y).apply(null, a),
  newApply: (a) => Reflect.construct(function (...r) { this.n = r.length; }, a).n,
  destructure: (a) => { const [p, q, ...r] = a; return p + ':' + q + ':' + r.length + ':' + r[r.length - 1]; },
  json: (a) => JSON.stringify(a).length + ':' + JSON.stringify(a, (k, v) => v).length,
  join: (a) => a.join().length + ':' + String(a).length + ':' + a.toString().slice(0, 9),
  concat: (a) => h(a.concat(a, [1])) + ':' + h([0].concat(a)),
  slice: (a) => h(a.slice(10, 4000)) + ':' + h(a.slice(-5)),
  splice: (a) => h(a.splice(10, 4100, 1, 2)) + ':' + h(a),
  shift: (a) => a.shift() + ':' + a.shift() + ':' + h(a),
  unshift: (a) => a.unshift(9, 8) + ':' + a[0] + ':' + a[2] + ':' + h(a),
  pop: (a) => a.pop() + ':' + a.length + ':' + a.push(5) + ':' + a[a.length - 1],
  reverse: (a) => h(a.reverse()) + ':' + a[0],
  sort: (a) => h(a.sort((x, y) => y - x)) + ':' + h(a.sort()),
  fill: (a) => h(a.fill(3, 2, 4500)) + ':' + a[1] + ':' + a[4500],
  copyWithin: (a) => h(a.copyWithin(1, 100, 4300)),
  search: (a) => [a.indexOf(N - 1), a.lastIndexOf(4097), a.includes(4096), a.includes(-1), a.at(-1), a.find((x) => x === 4100), a.findLast((x) => x < 5), a.findIndex((x) => x === 4096)].join(),
  callbacks: (a) => { let c = 0; a.forEach((v, i, r) => { c += i + (r === a ? 1 : 0); }); return c + ':' + h(a.map((x) => x + 1)) + ':' + h(a.filter((x) => x % 2)) + ':' + a.reduce((s, v) => (s + v) | 0, 0) + ':' + a.reduceRight((s, v) => (s - v) | 0, 0) + ':' + a.some((x) => x > 4999) + ':' + a.every((x) => x > 0); },
  callbackMutates: (a) => { let c = 0; a.forEach((v, i) => { if (i === 4090) { a.length = 4095; a[4094] = 77; } c = (c + v) | 0; }); return c + ':' + a.length; },
  callbackShifts: (a) => { let c = 0; a.forEach((v, i) => { if (i < 3) a.shift(); c = (c + v) | 0; }); return c + ':' + a.length; },
  iterators: (a) => { const it = a[Symbol.iterator](); it.next(); const x = it.next().value; a.length = 2; return x + ':' + it.next().value + ':' + it.next().done + ':' + [...a.keys()].length + ':' + [...a.entries()].length; },
  freeze: (a) => { Object.freeze(a); let t = 0; try { 'use strict'; a.push(1); } catch (e) { t = e.constructor.name; } a[0] = 123; return t + ':' + a[0] + ':' + Object.isFrozen(a) + ':' + a.length; },
  seal: (a) => { Object.seal(a); a[1] = 55; delete a[2]; return a[1] + ':' + (2 in a) + ':' + Object.isSealed(a); },
  defineProp: (a) => { Object.defineProperty(a, 4097, { get() { return 42; }, configurable: true }); return a[4097] + ':' + h(a.slice(4096, 4099)) + ':' + a.indexOf(42); },
  proto: (a) => { delete a[4098]; Object.setPrototypeOf(a, Object.assign(Object.create(Array.prototype), { 4098: 'p' })); return a[4098] + ':' + (4098 in a) + ':' + a.hasOwnProperty(4098) + ':' + a.slice(4098, 4099)[0]; },
  structured: (a) => new Map(a.map((x) => [x, x])).size + ':' + new Set(a).size + ':' + Array.from(a).length + ':' + Array.of(...a).length,
  withInst: (a) => (Array.isArray(a)) + ':' + h(a.toReversed()) + ':' + h(a.toSorted((x, y) => y - x)) + ':' + h(a.toSpliced(1, 4200)) + ':' + a.with(4100, 1)[4100],
  typed: (a) => new Float64Array(a)[N - 1] + ':' + Int32Array.from(a).length,
  strFromCode: (a) => String.fromCharCode.apply(null, a.slice(65, 70)) + ':' + 'x'.concat(...a.slice(0, 2)),
  bindArgs: (a) => ((...r) => r.length).bind(null, ...a)(1),
};
for (const m in modes) for (const o in ops) {
  let r; const a = modes[m]();
  try { r = ops[o](a); } catch (e) { r = 'E:' + e.constructor.name; }
  log.push(m + '.' + o + '=' + r);
}
"""";

    private const string Expected = """"
pushed.index=4999undefined
pushed.write=5000:351717990
pushed.writePast=5006:true:false:undefined
pushed.lengthCut=100:58926130:undefined
pushed.lengthGrow=5010:false
pushed.deleteOne=false:undefined:4:4999
pushed.keys=5000:0:4999
pushed.own=5001:true:false:7
pushed.entries=5000:5
pushed.forIn=12497500
pushed.forOf=12497500
pushed.spreadLit=5001:800036541
pushed.spreadCall=4999:5000
pushed.apply=0:4999:1
pushed.newApply=5000
pushed.destructure=0:1:4998:4999
pushed.json=23891:23891
pushed.join=23889:23889:0,1,2,3,4
pushed.concat=10001:719090041:5001:1826922948
pushed.slice=3990:-1164775029:5:471817989
pushed.splice=4100:1082048130:902:-1359395869
pushed.shift=0:1:4998:-1475094909
pushed.unshift=5002:9:0:5002:-579304477
pushed.pop=4999:4999:5000:5
pushed.reverse=5000:1573804476:4999
pushed.sort=5000:1573804476:5000:-1277206294
pushed.fill=5000:-2066199397:1:4500
pushed.copyWithin=5000:-1577976252
pushed.search=4999,4097,true,false,4999,4100,4,4096
pushed.callbacks=12502500:5000:800041540:2500:-2136398332:12497500:-12497500:false:false
pushed.callbackMutates=8378448:4095
pushed.callbackShifts=12497491:4997
pushed.iterators=1:undefined:true:2:2
pushed.freeze=TypeError:0:true:5000
pushed.seal=55:true:true
pushed.defineProp=42:3:3941656:42
pushed.proto=p:true:false:p
pushed.structured=5000:5000:5000:5000
pushed.withInst=true:5000:1573804476:5000:1573804476:800:-6402312:1
pushed.typed=4999:5000
pushed.strFromCode=ABCDE:x01
pushed.bindArgs=5001
pushedBig.index=49995000
pushedBig.write=20000:1998866866
pushedBig.writePast=20000:true:true:5002
pushedBig.lengthCut=100:58926130:undefined
pushedBig.lengthGrow=5010:true
pushedBig.deleteOne=false:undefined:4:19999
pushedBig.keys=20000:0:19999
pushedBig.own=20001:true:true:7
pushedBig.entries=20000:5
pushedBig.forIn=199990000
pushedBig.forOf=199990000
pushedBig.spreadLit=20001:305978097
pushedBig.spreadCall=19999:20000
pushedBig.apply=0:4999:1
pushedBig.newApply=20000
pushedBig.destructure=0:1:19998:19999
pushedBig.json=108891:108891
pushedBig.join=108889:108889:0,1,2,3,4
pushedBig.concat=40001:-2081557023:20001:-1237055728
pushedBig.slice=3990:-1164775029:5:1901491101
pushedBig.splice=4100:1082048130:15902:1452166191
pushedBig.shift=0:1:19998:1181810383
pushedBig.unshift=20002:9:0:20002:122522671
pushedBig.pop=19999:19999:20000:5
pushedBig.reverse=20000:1430460144:19999
pushedBig.sort=20000:1430460144:20000:-167881622
pushedBig.fill=20000:926100711:1:4500
pushedBig.copyWithin=20000:-449059952
pushedBig.search=4999,4097,true,false,19999,4100,4,4096
pushedBig.callbacks=200010000:20000:305998096:10000:-1573873648:199990000:-199990000:true:false
pushedBig.callbackMutates=8378448:4095
pushedBig.callbackShifts=199989991:19997
pushedBig.iterators=1:undefined:true:2:2
pushedBig.freeze=TypeError:0:true:20000
pushedBig.seal=55:true:true
pushedBig.defineProp=42:3:3941656:42
pushedBig.proto=p:true:false:p
pushedBig.structured=20000:20000:20000:20000
pushedBig.withInst=true:20000:1430460144:20000:1430460144:15800:608573508:1
pushedBig.typed=4999:20000
pushedBig.strFromCode=ABCDE:x01
pushedBig.bindArgs=20001
filtered.index=4999undefined
filtered.write=5000:351717990
filtered.writePast=5006:true:false:undefined
filtered.lengthCut=100:58926130:undefined
filtered.lengthGrow=5010:false
filtered.deleteOne=false:undefined:4:4999
filtered.keys=5000:0:4999
filtered.own=5001:true:false:7
filtered.entries=5000:5
filtered.forIn=12497500
filtered.forOf=12497500
filtered.spreadLit=5001:800036541
filtered.spreadCall=4999:5000
filtered.apply=0:4999:1
filtered.newApply=5000
filtered.destructure=0:1:4998:4999
filtered.json=23891:23891
filtered.join=23889:23889:0,1,2,3,4
filtered.concat=10001:719090041:5001:1826922948
filtered.slice=3990:-1164775029:5:471817989
filtered.splice=4100:1082048130:902:-1359395869
filtered.shift=0:1:4998:-1475094909
filtered.unshift=5002:9:0:5002:-579304477
filtered.pop=4999:4999:5000:5
filtered.reverse=5000:1573804476:4999
filtered.sort=5000:1573804476:5000:-1277206294
filtered.fill=5000:-2066199397:1:4500
filtered.copyWithin=5000:-1577976252
filtered.search=4999,4097,true,false,4999,4100,4,4096
filtered.callbacks=12502500:5000:800041540:2500:-2136398332:12497500:-12497500:false:false
filtered.callbackMutates=8378448:4095
filtered.callbackShifts=12497491:4997
filtered.iterators=1:undefined:true:2:2
filtered.freeze=TypeError:0:true:5000
filtered.seal=55:true:true
filtered.defineProp=42:3:3941656:42
filtered.proto=p:true:false:p
filtered.structured=5000:5000:5000:5000
filtered.withInst=true:5000:1573804476:5000:1573804476:800:-6402312:1
filtered.typed=4999:5000
filtered.strFromCode=ABCDE:x01
filtered.bindArgs=5001
flatted.index=4999undefined
flatted.write=5000:351717990
flatted.writePast=5006:true:false:undefined
flatted.lengthCut=100:58926130:undefined
flatted.lengthGrow=5010:false
flatted.deleteOne=false:undefined:4:4999
flatted.keys=5000:0:4999
flatted.own=5001:true:false:7
flatted.entries=5000:5
flatted.forIn=12497500
flatted.forOf=12497500
flatted.spreadLit=5001:800036541
flatted.spreadCall=4999:5000
flatted.apply=0:4999:1
flatted.newApply=5000
flatted.destructure=0:1:4998:4999
flatted.json=23891:23891
flatted.join=23889:23889:0,1,2,3,4
flatted.concat=10001:719090041:5001:1826922948
flatted.slice=3990:-1164775029:5:471817989
flatted.splice=4100:1082048130:902:-1359395869
flatted.shift=0:1:4998:-1475094909
flatted.unshift=5002:9:0:5002:-579304477
flatted.pop=4999:4999:5000:5
flatted.reverse=5000:1573804476:4999
flatted.sort=5000:1573804476:5000:-1277206294
flatted.fill=5000:-2066199397:1:4500
flatted.copyWithin=5000:-1577976252
flatted.search=4999,4097,true,false,4999,4100,4,4096
flatted.callbacks=12502500:5000:800041540:2500:-2136398332:12497500:-12497500:false:false
flatted.callbackMutates=8378448:4095
flatted.callbackShifts=12497491:4997
flatted.iterators=1:undefined:true:2:2
flatted.freeze=TypeError:0:true:5000
flatted.seal=55:true:true
flatted.defineProp=42:3:3941656:42
flatted.proto=p:true:false:p
flatted.structured=5000:5000:5000:5000
flatted.withInst=true:5000:1573804476:5000:1573804476:800:-6402312:1
flatted.typed=4999:5000
flatted.strFromCode=ABCDE:x01
flatted.bindArgs=5001
flatMapped.index=4999undefined
flatMapped.write=5000:351717990
flatMapped.writePast=5006:true:false:undefined
flatMapped.lengthCut=100:58926130:undefined
flatMapped.lengthGrow=5010:false
flatMapped.deleteOne=false:undefined:4:4999
flatMapped.keys=5000:0:4999
flatMapped.own=5001:true:false:7
flatMapped.entries=5000:5
flatMapped.forIn=12497500
flatMapped.forOf=12497500
flatMapped.spreadLit=5001:800036541
flatMapped.spreadCall=4999:5000
flatMapped.apply=0:4999:1
flatMapped.newApply=5000
flatMapped.destructure=0:1:4998:4999
flatMapped.json=23891:23891
flatMapped.join=23889:23889:0,1,2,3,4
flatMapped.concat=10001:719090041:5001:1826922948
flatMapped.slice=3990:-1164775029:5:471817989
flatMapped.splice=4100:1082048130:902:-1359395869
flatMapped.shift=0:1:4998:-1475094909
flatMapped.unshift=5002:9:0:5002:-579304477
flatMapped.pop=4999:4999:5000:5
flatMapped.reverse=5000:1573804476:4999
flatMapped.sort=5000:1573804476:5000:-1277206294
flatMapped.fill=5000:-2066199397:1:4500
flatMapped.copyWithin=5000:-1577976252
flatMapped.search=4999,4097,true,false,4999,4100,4,4096
flatMapped.callbacks=12502500:5000:800041540:2500:-2136398332:12497500:-12497500:false:false
flatMapped.callbackMutates=8378448:4095
flatMapped.callbackShifts=12497491:4997
flatMapped.iterators=1:undefined:true:2:2
flatMapped.freeze=TypeError:0:true:5000
flatMapped.seal=55:true:true
flatMapped.defineProp=42:3:3941656:42
flatMapped.proto=p:true:false:p
flatMapped.structured=5000:5000:5000:5000
flatMapped.withInst=true:5000:1573804476:5000:1573804476:800:-6402312:1
flatMapped.typed=4999:5000
flatMapped.strFromCode=ABCDE:x01
flatMapped.bindArgs=5001
arrayFrom.index=4999undefined
arrayFrom.write=5000:351717990
arrayFrom.writePast=5006:true:false:undefined
arrayFrom.lengthCut=100:58926130:undefined
arrayFrom.lengthGrow=5010:false
arrayFrom.deleteOne=false:undefined:4:4999
arrayFrom.keys=5000:0:4999
arrayFrom.own=5001:true:false:7
arrayFrom.entries=5000:5
arrayFrom.forIn=12497500
arrayFrom.forOf=12497500
arrayFrom.spreadLit=5001:800036541
arrayFrom.spreadCall=4999:5000
arrayFrom.apply=0:4999:1
arrayFrom.newApply=5000
arrayFrom.destructure=0:1:4998:4999
arrayFrom.json=23891:23891
arrayFrom.join=23889:23889:0,1,2,3,4
arrayFrom.concat=10001:719090041:5001:1826922948
arrayFrom.slice=3990:-1164775029:5:471817989
arrayFrom.splice=4100:1082048130:902:-1359395869
arrayFrom.shift=0:1:4998:-1475094909
arrayFrom.unshift=5002:9:0:5002:-579304477
arrayFrom.pop=4999:4999:5000:5
arrayFrom.reverse=5000:1573804476:4999
arrayFrom.sort=5000:1573804476:5000:-1277206294
arrayFrom.fill=5000:-2066199397:1:4500
arrayFrom.copyWithin=5000:-1577976252
arrayFrom.search=4999,4097,true,false,4999,4100,4,4096
arrayFrom.callbacks=12502500:5000:800041540:2500:-2136398332:12497500:-12497500:false:false
arrayFrom.callbackMutates=8378448:4095
arrayFrom.callbackShifts=12497491:4997
arrayFrom.iterators=1:undefined:true:2:2
arrayFrom.freeze=TypeError:0:true:5000
arrayFrom.seal=55:true:true
arrayFrom.defineProp=42:3:3941656:42
arrayFrom.proto=p:true:false:p
arrayFrom.structured=5000:5000:5000:5000
arrayFrom.withInst=true:5000:1573804476:5000:1573804476:800:-6402312:1
arrayFrom.typed=4999:5000
arrayFrom.strFromCode=ABCDE:x01
arrayFrom.bindArgs=5001
shifted.index=4999undefined
shifted.write=5000:351717990
shifted.writePast=5006:true:false:undefined
shifted.lengthCut=100:58926130:undefined
shifted.lengthGrow=5010:false
shifted.deleteOne=false:undefined:4:4999
shifted.keys=5000:0:4999
shifted.own=5001:true:false:7
shifted.entries=5000:5
shifted.forIn=12497500
shifted.forOf=12497500
shifted.spreadLit=5001:800036541
shifted.spreadCall=4999:5000
shifted.apply=0:4999:1
shifted.newApply=5000
shifted.destructure=0:1:4998:4999
shifted.json=23891:23891
shifted.join=23889:23889:0,1,2,3,4
shifted.concat=10001:719090041:5001:1826922948
shifted.slice=3990:-1164775029:5:471817989
shifted.splice=4100:1082048130:902:-1359395869
shifted.shift=0:1:4998:-1475094909
shifted.unshift=5002:9:0:5002:-579304477
shifted.pop=4999:4999:5000:5
shifted.reverse=5000:1573804476:4999
shifted.sort=5000:1573804476:5000:-1277206294
shifted.fill=5000:-2066199397:1:4500
shifted.copyWithin=5000:-1577976252
shifted.search=4999,4097,true,false,4999,4100,4,4096
shifted.callbacks=12502500:5000:800041540:2500:-2136398332:12497500:-12497500:false:false
shifted.callbackMutates=8378448:4095
shifted.callbackShifts=12497491:4997
shifted.iterators=1:undefined:true:2:2
shifted.freeze=TypeError:0:true:5000
shifted.seal=55:true:true
shifted.defineProp=42:3:3941656:42
shifted.proto=p:true:false:p
shifted.structured=5000:5000:5000:5000
shifted.withInst=true:5000:1573804476:5000:1573804476:800:-6402312:1
shifted.typed=4999:5000
shifted.strFromCode=ABCDE:x01
shifted.bindArgs=5001
pushedShifted.index=4999undefined
pushedShifted.write=5000:351717990
pushedShifted.writePast=5006:true:false:undefined
pushedShifted.lengthCut=100:58926130:undefined
pushedShifted.lengthGrow=5010:false
pushedShifted.deleteOne=false:undefined:4:4999
pushedShifted.keys=5000:0:4999
pushedShifted.own=5001:true:false:7
pushedShifted.entries=5000:5
pushedShifted.forIn=12497500
pushedShifted.forOf=12497500
pushedShifted.spreadLit=5001:800036541
pushedShifted.spreadCall=4999:5000
pushedShifted.apply=0:4999:1
pushedShifted.newApply=5000
pushedShifted.destructure=0:1:4998:4999
pushedShifted.json=23891:23891
pushedShifted.join=23889:23889:0,1,2,3,4
pushedShifted.concat=10001:719090041:5001:1826922948
pushedShifted.slice=3990:-1164775029:5:471817989
pushedShifted.splice=4100:1082048130:902:-1359395869
pushedShifted.shift=0:1:4998:-1475094909
pushedShifted.unshift=5002:9:0:5002:-579304477
pushedShifted.pop=4999:4999:5000:5
pushedShifted.reverse=5000:1573804476:4999
pushedShifted.sort=5000:1573804476:5000:-1277206294
pushedShifted.fill=5000:-2066199397:1:4500
pushedShifted.copyWithin=5000:-1577976252
pushedShifted.search=4999,4097,true,false,4999,4100,4,4096
pushedShifted.callbacks=12502500:5000:800041540:2500:-2136398332:12497500:-12497500:false:false
pushedShifted.callbackMutates=8378448:4095
pushedShifted.callbackShifts=12497491:4997
pushedShifted.iterators=1:undefined:true:2:2
pushedShifted.freeze=TypeError:0:true:5000
pushedShifted.seal=55:true:true
pushedShifted.defineProp=42:3:3941656:42
pushedShifted.proto=p:true:false:p
pushedShifted.structured=5000:5000:5000:5000
pushedShifted.withInst=true:5000:1573804476:5000:1573804476:800:-6402312:1
pushedShifted.typed=4999:5000
pushedShifted.strFromCode=ABCDE:x01
pushedShifted.bindArgs=5001
"""";

    [Fact]
    public void EveryOperationOnEveryStorageModeMatchesNode()
    {
        using var engine = new JsEngine();
        engine.Evaluate("var log = [];\n" + Script);
        var got = engine.Evaluate("log.join('\\n')").ToString()!.Split('\n');
        // a Windows checkout may turn this source file's line endings, and so this literal's, into CRLF
        var exp = Expected.ReplaceLineEndings("\n").Split('\n');
        var wrong = exp.Zip(got).Where(p => p.First != p.Second).Select(p => $"expected {p.First} got {p.Second}").ToArray();
        Assert.True(wrong.Length == 0 && exp.Length == got.Length, $"{wrong.Length} of {exp.Length} differ (got {got.Length}):\n" + string.Join("\n", wrong.Take(40)));
    }
}
