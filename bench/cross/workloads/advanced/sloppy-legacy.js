// Sloppy-mode / legacy features: arguments object, with, implicit globals, function hoisting, var closures, getter via __defineGetter__.
function sum() { var s = 0; for (var i = 0; i < arguments.length; i++) s += arguments[i]; return s; }
function aliased(a, b) { arguments[0] = 10; return a + b + arguments.length; }
function main() {
  var t = 0;
  for (var i = 0; i < 6000; i++) {
    t += sum(i, 1, 2, 3) + aliased(1, 2);
    with ({ p: i, q: 2 }) { t += p * q; }
    var o = {}; o.__defineGetter__('g', function () { return 7; }); t += o.g;
    t += hoisted() + (function () { return typeof this; })().length;
  }
  implicitGlobal = 5; t += implicitGlobal;
  function hoisted() { return 1; }
  var fs = []; for (var j = 0; j < 3; j++) fs.push(function () { return j; });
  return [t, fs.map(function (f) { return f(); }).join('')].join(',');
}
