using Jaunt;
using Xunit;

namespace Jaunt.Tests;

/// <summary>Intl.Collator, Segmenter, ListFormat, DisplayNames, RelativeTimeFormat and the locale-sensitive String methods.</summary>
public class IntlTextTests
{
    private static string Eval(string code)
    {
        using var engine = new JsEngine();
        return engine.Evaluate(code).ToString()!;
    }

    private static bool HasIntl()
    {
        using var engine = new JsEngine();
        return engine.Evaluate("typeof Intl").ToString() == "object";
    }

    [Fact]
    public void CollatorSortsAndCompares()
    {
        if (!HasIntl()) return;
        Assert.Equal("a,ä,b,z", Eval("['z','b','ä','a'].sort(new Intl.Collator('de').compare).join()"));
        Assert.Equal("a,b,z,ä", Eval("['z','b','ä','a'].sort(new Intl.Collator('sv').compare).join()"));
        Assert.Equal("2,10", Eval("['10','2'].sort(new Intl.Collator('en', {numeric: true}).compare).join()"));
        Assert.Equal("0,-1", Eval("var c = new Intl.Collator('en', {sensitivity: 'base'}); [c.compare('a','A'), c.compare('a','b')].join()"));
        Assert.Equal("0", Eval("'\\u0041\\u030A'.localeCompare('\\u00C5')"));
        Assert.Equal("en-u-kn,true", Eval("var o = new Intl.Collator('en-u-kn').resolvedOptions(); [o.locale, o.numeric].join()"));
    }

    [Fact]
    public void LocaleCaseMapping()
    {
        if (!HasIntl()) return;
        Assert.Equal("İ,i̇,ı", Eval("['i'.toLocaleUpperCase('tr'), 'İ'.toLocaleLowerCase('en'), 'I'.toLocaleLowerCase('tr')].join()"));
    }

    [Fact]
    public void SegmenterSplitsWords()
    {
        if (!HasIntl()) return;
        Assert.Equal("Hello:true| :false|world:true|!:false",
            Eval("[...new Intl.Segmenter('en', {granularity: 'word'}).segment('Hello world!')].map(s => s.segment + ':' + s.isWordLike).join('|')"));
        Assert.Equal("6,world", Eval("var s = new Intl.Segmenter('en', {granularity: 'word'}).segment('Hello world!').containing(8); s.index + ',' + s.segment"));
        Assert.Equal("3", Eval("[...new Intl.Segmenter().segment('a👍🏽b')].length"));
    }

    [Fact]
    public void ListFormatJoins()
    {
        if (!HasIntl()) return;
        Assert.Equal("a, b, and c", Eval("new Intl.ListFormat('en').format(['a','b','c'])"));
        Assert.Equal("a or b", Eval("new Intl.ListFormat('en', {type: 'disjunction'}).format(['a','b'])"));
        Assert.Equal("element:a|literal: and |element:b",
            Eval("new Intl.ListFormat('en').formatToParts(['a','b']).map(p => p.type + ':' + p.value).join('|')"));
    }

    [Fact]
    public void DisplayNamesLookUpNames()
    {
        if (!HasIntl()) return;
        Assert.Equal("German,Germany,Latin,US Dollar,Gregorian Calendar,year",
            Eval("['language:de','region:DE','script:Latn','currency:USD','calendar:gregory','dateTimeField:year'].map(s => { var [t, c] = s.split(':'); return new Intl.DisplayNames('en', {type: t}).of(c); }).join()"));
        Assert.Equal("undefined,ZZZ", Eval("[String(new Intl.DisplayNames('en', {type: 'currency', fallback: 'none'}).of('ZZZ')), new Intl.DisplayNames('en', {type: 'currency'}).of('zzz')].join()"));
    }

    [Fact]
    public void RelativeTimeFormatFormats()
    {
        if (!HasIntl()) return;
        Assert.Equal("in 3 days,1 day ago,yesterday,in 1,234.5 hours",
            Eval("var f = new Intl.RelativeTimeFormat('en'); [f.format(3, 'day'), f.format(-1, 'days'), new Intl.RelativeTimeFormat('en', {numeric: 'auto'}).format(-1, 'day'), f.format(1234.5, 'hour')].join()"));
        Assert.Equal("literal:in |integer:1:hour|group:,:hour|integer:234:hour|decimal:.:hour|fraction:5:hour|literal: hours",
            Eval("new Intl.RelativeTimeFormat('en').formatToParts(1234.5, 'hours').map(p => [p.type, p.value, p.unit].filter(x => x !== undefined).join(':')).join('|')"));
        Assert.Equal("arab", Eval("new Intl.RelativeTimeFormat('ar-u-nu-arab').resolvedOptions().numberingSystem"));
    }
}
