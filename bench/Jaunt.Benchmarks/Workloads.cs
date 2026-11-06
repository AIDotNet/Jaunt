namespace Jaunt.Benchmarks;

/// <summary>Shared workload sources. Each defines `main(n)` and is semantically identical across engines.</summary>
public static class Workloads
{
    public const string NumericLoop = """
        function main(n) { let s = 0; for (let i = 0; i < 100000; i++) { s = (s + i * 3) % 1000003; } return s; }
        """;

    public const string Fib = """
        function fib(n) { return n < 2 ? n : fib(n - 1) + fib(n - 2); }
        function main(n) { return fib(20); }
        """;

    public const string Objects = """
        function Point(x, y) { this.x = x; this.y = y; }
        Point.prototype.len2 = function () { return this.x * this.x + this.y * this.y; };
        function main(n) {
          let total = 0;
          for (let i = 0; i < 20000; i++) { const p = new Point(i, i + 1); const o = { a: p.x, b: p.y, c: i }; total += p.len2() % 7 + o.a - o.b + o.c; }
          return total;
        }
        """;

    public const string Arrays = """
        function main(n) {
          const a = [];
          for (let i = 0; i < 10000; i++) a.push(i);
          const b = a.map(x => x * 2).filter(x => x % 3 === 0);
          let s = b.reduce((acc, x) => acc + x, 0);
          for (const v of a) s += v & 1;
          return s + a.indexOf(9999) + a.slice(10, 20).length;
        }
        """;

    public const string Strings = """
        function main(n) {
          let parts = [];
          for (let i = 0; i < 2000; i++) parts.push('item-' + i + ':' + (i % 7));
          const joined = parts.join(',');
          const split = joined.split(',');
          let h = 0;
          for (const s of split) h = (h * 31 + s.length + s.charCodeAt(0)) | 0;
          return h + joined.indexOf('item-1999') + `${split[5]}`.toUpperCase().length;
        }
        """;

    public const string Classes = """
        class Account { #balance = 0; constructor(id) { this.id = id; } deposit(v) { this.#balance += v; return this; } get balance() { return this.#balance; } }
        class Savings extends Account { constructor(id, rate) { super(id); this.rate = rate; } accrue() { return this.deposit(this.balance * this.rate); } }
        function main(n) { let t = 0; for (let i = 0; i < 5000; i++) { const a = new Savings(i, 0.01); a.deposit(100).accrue(); t += a.balance; } return Math.round(t); }
        """;

    public const string Json = """
        function main(n) {
          const payload = { user: { id: 42, name: 'Ada', tags: ['a', 'b', 'c'] }, items: [] };
          for (let i = 0; i < 200; i++) payload.items.push({ sku: 'S' + i, qty: i % 5, price: i * 1.25, flags: { gift: i % 2 === 0 } });
          const text = JSON.stringify(payload);
          const back = JSON.parse(text);
          return back.items.length + back.items[199].price + text.length;
        }
        """;

    public const string Small = """
        function main(n) { return n * 2 + 1; }
        """;

    public const string PluginModule = """
        export function transform(input) {
          const items = input.items.filter(i => i.qty > 0).map(i => ({ sku: i.sku, total: i.qty * i.price }));
          return { id: input.id, count: items.length, total: items.reduce((s, i) => s + i.total, 0) };
        }
        """;

    public const string PluginInputJson = """
        {"id":"order-1","items":[{"sku":"a","qty":1,"price":2.5},{"sku":"b","qty":0,"price":1},{"sku":"c","qty":3,"price":4}]}
        """;
}
