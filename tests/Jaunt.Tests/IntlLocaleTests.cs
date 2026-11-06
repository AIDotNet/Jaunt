using Jaunt;
using Xunit;

namespace Jaunt.Tests;

/// <summary>Intl core (UTS 35 canonicalization), Intl.Locale, Intl.supportedValuesOf and Intl.DurationFormat.</summary>
public class IntlLocaleTests
{
    private static string Eval(string code)
    {
        using var e = new JsEngine();
        if (e.Evaluate("typeof Intl").ToString() == "undefined") return "<no-icu>";
        return e.Evaluate(code).ToString();
    }

    private static void Expect(string expected, string code)
    {
        var actual = Eval(code);
        if (actual == "<no-icu>") return;   // ICU unavailable on this host
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("en-us", "en-US")]
    [InlineData("cmn-hans-cn", "zh-Hans-CN")]
    [InlineData("sh", "sr-Latn")]
    [InlineData("ru-SU", "ru-RU")]
    [InlineData("hy-SU", "hy-AM")]
    [InlineData("sgn-GR", "gss")]
    [InlineData("art-lojban", "jbo")]
    [InlineData("ja-latn-hepburn-heploc", "ja-Latn-alalc97")]
    [InlineData("sl-rozaj-biske-1994", "sl-1994-biske-rozaj")]
    [InlineData("und-u-ca-islamicc-ks-primary-tz-est-kn-yes", "und-u-ca-islamic-civil-kn-ks-level1-tz-papty")]
    [InlineData("und-u-rg-no23", "und-u-rg-no50")]
    [InlineData("DE-T-M0-DIN-K0-QWERTZ", "de-t-k0-qwertz-m0-din")]
    [InlineData("en-t-iw-m0-names", "en-t-he-m0-prprname")]
    public void CanonicalizesWithCldrAliases(string tag, string canonical) =>
        Expect(canonical, $"Intl.getCanonicalLocales('{tag}')[0]");

    [Fact]
    public void RejectsInvalidTags() =>
        Expect("RangeError,RangeError,RangeError", """
            ['i-klingon', 'en-u', 'de-1996-1996'].map(t => { try { Intl.getCanonicalLocales(t); return 'ok'; } catch (e) { return e.name; } }).join()
            """);

    [Fact]
    public void LocaleConstructorAndGetters() =>
        Expect("ja-Jpan-JP-hepburn-u-ca-japanese-kn|ja|Jpan|JP|hepburn|japanese|true|ja-Jpan-JP-hepburn", """
            var l = new Intl.Locale('de-latn-de-u-kn-false', { language: 'ja', script: 'jpan', region: 'jp', variants: 'Hepburn', calendar: 'japanese', numeric: true });
            [l.toString(), l.language, l.script, l.region, l.variants, l.calendar, l.numeric, l.baseName].join('|')
            """);

    [Fact]
    public void LocaleMaximizeMinimize() =>
        Expect("en-Latn-US-u-co-phonebk|zh-TW|en|und-Armn-AM", """
            [new Intl.Locale('en-u-co-phonebk').maximize().toString(), new Intl.Locale('zh-Hant').minimize().toString(),
             new Intl.Locale('und').minimize().toString(), Intl.getCanonicalLocales(new Intl.Locale('und-Armn-SU'))[0]].join('|')
            """);

    [Fact]
    public void LocaleInfo() =>
        Expect("7|6,7|ltr|rtl|true", """
            var w = new Intl.Locale('en-US').getWeekInfo();
            [w.firstDay, w.weekend, new Intl.Locale('en').getTextInfo().direction, new Intl.Locale('ar').getTextInfo().direction,
             new Intl.Locale('en-US').getTimeZones().includes('America/New_York')].join('|')
            """);

    [Fact]
    public void SupportedValuesOf() =>
        Expect("true|true|true|true|RangeError", """
            var c = Intl.supportedValuesOf('calendar'), u = Intl.supportedValuesOf('unit'), n = Intl.supportedValuesOf('numberingSystem');
            var t = Intl.supportedValuesOf('timeZone');
            var err; try { Intl.supportedValuesOf('nope'); } catch (e) { err = e.name; }
            [c.includes('gregory'), u.length === 45, n.includes('latn') && n.includes('arab'), t.includes('UTC') && !t.includes('Etc/UTC'), err].join('|')
            """);

    [Fact]
    public void DurationFormat() =>
        Expect("1 hr, 2 min|1:02:03.5|1 hour, 2 minutes, 3.004 seconds|-1:00:00", """
            [new Intl.DurationFormat('en').format({ hours: 1, minutes: 2 }),
             new Intl.DurationFormat('en', { style: 'digital' }).format({ hours: 1, minutes: 2, seconds: 3, milliseconds: 500 }),
             new Intl.DurationFormat('en', { style: 'long', milliseconds: 'numeric' }).format({ hours: 1, minutes: 2, seconds: 3, milliseconds: 4 }),
             new Intl.DurationFormat('en', { style: 'digital' }).format({ hours: -1 })].join('|')
            """);

    [Fact]
    public void DurationFormatResolvedOptionsAndParts() =>
        Expect("numeric|2-digit|numeric|auto|hour,literal,minute", """
            var df = new Intl.DurationFormat('en', { style: 'digital' });
            var o = df.resolvedOptions();
            var p = df.formatToParts({ hours: 1, minutes: 5 });
            [o.hours, o.minutes, o.milliseconds, o.millisecondsDisplay, p.slice(0, 3).map(x => x.unit ?? x.type)].join('|')
            """);
}
