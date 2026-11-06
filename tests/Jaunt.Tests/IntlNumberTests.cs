using Jaunt;
using Xunit;

namespace Jaunt.Tests;

/// <summary>Intl.NumberFormat, Intl.PluralRules and the locale-sensitive toLocaleString methods (skipped when ICU is unavailable).</summary>
public class IntlNumberTests
{
    private static string? Eval(string src)
    {
        using var e = new JsEngine(new JsEngineOptions { Limits = new JsLimits { Timeout = TimeSpan.FromSeconds(10) } });
        if (e.Evaluate("typeof Intl").ToString() == "undefined") return null;
        return e.Evaluate(src).ToString();
    }

    private static void Check(string src, string expected)
    {
        var actual = Eval(src);
        if (actual != null) Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("new Intl.NumberFormat('en-US').format(1234567.891)", "1,234,567.891")]
    [InlineData("new Intl.NumberFormat('de-DE', { style: 'currency', currency: 'EUR' }).format(-1234.5)", "-1.234,50 €")]
    [InlineData("new Intl.NumberFormat('en', { notation: 'compact' }).format(1234567)", "1.2M")]
    [InlineData("new Intl.NumberFormat('en', { style: 'percent' }).format(0.256)", "26%")]
    [InlineData("new Intl.NumberFormat('en', { style: 'unit', unit: 'kilometer-per-hour', unitDisplay: 'long' }).format(50)", "50 kilometers per hour")]
    [InlineData("new Intl.NumberFormat('en', { maximumFractionDigits: 20 }).format('-1.0000000000000001')", "-1.0000000000000001")]
    [InlineData("new Intl.NumberFormat('en').format(12345678901234567890n)", "12,345,678,901,234,567,890")]
    [InlineData("new Intl.NumberFormat('en', { roundingIncrement: 25, maximumFractionDigits: 2, minimumFractionDigits: 2 }).format('1.3125')", "1.25")]
    [InlineData("new Intl.NumberFormat('en', { trailingZeroDisplay: 'stripIfInteger', minimumFractionDigits: 2 }).format(1)", "1")]
    [InlineData("new Intl.NumberFormat('en-u-nu-arab').format(123)", "١٢٣")]
    [InlineData("new Intl.NumberFormat('en').format(NaN) + new Intl.NumberFormat('en').format(-Infinity)", "NaN-∞")]
    [InlineData("new Intl.NumberFormat('en', { style: 'currency', currency: 'USD', maximumFractionDigits: 0 }).formatRange(2.9, 3.1)", "~$3")]
    public void Format(string src, string expected) => Check(src, expected);

    [Fact]
    public void FormatToParts() => Check(
        "new Intl.NumberFormat('en').formatToParts(-1234.5).map(p => p.type + ':' + p.value).join('|')",
        "minusSign:-|integer:1|group:,|integer:234|decimal:.|fraction:5");

    [Fact]
    public void FormatRangeToParts() => Check(
        "new Intl.NumberFormat('en').formatRangeToParts(3, 5).map(p => p.type + ':' + p.value + ':' + p.source).join('|')",
        "integer:3:startRange|literal:–:shared|integer:5:endRange");

    [Fact]
    public void ResolvedOptions() => Check(
        "Object.keys(new Intl.NumberFormat('en', { style: 'currency', currency: 'usd' }).resolvedOptions()).join() + '|' + " +
        "new Intl.NumberFormat('en', { style: 'currency', currency: 'usd' }).resolvedOptions().currency",
        "locale,numberingSystem,style,currency,currencyDisplay,currencySign,minimumIntegerDigits,minimumFractionDigits,maximumFractionDigits," +
        "useGrouping,notation,signDisplay,roundingIncrement,roundingMode,roundingPriority,trailingZeroDisplay|USD");

    [Fact]
    public void LegacyConstructor() => Check(
        "var o = Object.create(Intl.NumberFormat.prototype); var r = Intl.NumberFormat.call(o, 'en');" +
        "(r === o) + ',' + o.format(1234) + ',' + Object.getOwnPropertySymbols(o)[0].description",
        "true,1,234,IntlLegacyConstructedSymbol");

    [Fact]
    public void FormatGetterIsBoundAndCached() => Check(
        "var nf = new Intl.NumberFormat('en'); var f = nf.format; (f === nf.format) + ',' + f(1e6) + ',' + f.length + ',' + JSON.stringify(f.name)",
        "true,1,000,000,1,\"\"");

    [Theory]
    [InlineData("new Intl.PluralRules('en').select(1)", "one")]
    [InlineData("new Intl.PluralRules('en', { minimumFractionDigits: 1 }).select(1)", "other")]
    [InlineData("new Intl.PluralRules('en', { type: 'ordinal' }).select(22)", "two")]
    [InlineData("new Intl.PluralRules('fr', { notation: 'compact' }).select(1.5e6)", "many")]
    [InlineData("new Intl.PluralRules('ar').resolvedOptions().pluralCategories.join()", "zero,one,two,few,many,other")]
    [InlineData("new Intl.PluralRules('sl').selectRange(102, 201)", "few")]
    [InlineData("try { Intl.PluralRules() } catch (e) { e.name }", "TypeError")]
    public void PluralRules(string src, string expected) => Check(src, expected);

    [Theory]
    [InlineData("(1234.5).toLocaleString('de-DE', { style: 'currency', currency: 'EUR' })", "1.234,50 €")]
    [InlineData("(1234.5).toLocaleString('en-US')", "1,234.5")]
    [InlineData("(12345678901234567890n).toLocaleString('de')", "12.345.678.901.234.567.890")]
    [InlineData("[1234.5, 'x'].toLocaleString('en')", "1,234.5,x")]
    [InlineData("new Float64Array([1234.5]).toLocaleString('de')", "1.234,5")]
    [InlineData("try { (1).toLocaleString('en', { style: 'currency' }) } catch (e) { e.name }", "TypeError")]
    public void ToLocaleString(string src, string expected) => Check(src, expected);
}
