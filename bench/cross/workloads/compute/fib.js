// Recursive calls: call/return overhead, small-int arithmetic.
function fib(n) { return n < 2 ? n : fib(n - 1) + fib(n - 2); }
function main() { return String(fib(24)); }
