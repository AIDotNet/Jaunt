using Jaunt;
using Xunit;

namespace Jaunt.Tests;

/// <summary>Intl.DateTimeFormat and the ECMA-402 toLocaleString methods of Date and Temporal (requires the platform ICU).</summary>
public class IntlDateTimeTests
{
    private static string Eval(string src)
    {
        using var e = new JsEngine(new JsEngineOptions { Limits = new JsLimits { Timeout = TimeSpan.FromSeconds(10) } });
        // tolerate hosts without ICU: Intl is then absent
        if (e.Evaluate("typeof Intl").ToString() == "undefined") return "no-icu";
        return e.Evaluate(src).ToString();
    }

    private static void Check(string src, string expected)
    {
        var r = Eval(src);
        if (r != "no-icu") Assert.Equal(expected, r);
    }

    [Theory]
    [InlineData("new Intl.DateTimeFormat('en-US', { timeZone: 'UTC' }).format(Date.UTC(2019, 0, 3, 14, 5))", "1/3/2019")]
    [InlineData("new Intl.DateTimeFormat('en-US', { timeStyle: 'short', hourCycle: 'h23', timeZone: 'UTC' }).format(Date.UTC(2019, 0, 3, 14, 5))", "14:05")]
    [InlineData("new Intl.DateTimeFormat('en', { hour: 'numeric', minute: 'numeric', hourCycle: 'h24', timeZone: 'UTC' }).format(Date.UTC(2019, 0, 3, 0, 5))", "24:05")]
    [InlineData("new Intl.DateTimeFormat('de', { timeZone: 'UTC' }).format(Date.UTC(2019, 0, 3))", "3.1.2019")]
    [InlineData("new Intl.DateTimeFormat('en', { timeZone: 'utc' }).resolvedOptions().timeZone + ',' + new Intl.DateTimeFormat('en', { timeZone: 'asia/calcutta' }).resolvedOptions().timeZone + ',' + new Intl.DateTimeFormat('en', { timeZone: '+0530' }).resolvedOptions().timeZone", "UTC,Asia/Calcutta,+05:30")]
    [InlineData("var o = new Intl.DateTimeFormat('ja', { hour: 'numeric', hour12: true }).resolvedOptions(); o.hourCycle + ',' + o.hour12", "h11,true")]
    [InlineData("new Intl.DateTimeFormat('en', { second: 'numeric', fractionalSecondDigits: 2, timeZone: 'UTC' }).formatToParts(1789).map(p => p.type + '=' + p.value).join('|')", "second=1|literal=.|fractionalSecond=78")]
    [InlineData("new Intl.DateTimeFormat('en-US', { month: 'short', day: 'numeric', timeZone: 'UTC' }).formatRangeToParts(Date.UTC(2019, 0, 3), Date.UTC(2019, 0, 5)).map(p => p.source[0] + p.value).join('').replace(/\\s/g, '_')", "sJans_s3s_–_e5")]
    [InlineData("try { new Intl.DateTimeFormat('en', { timeZone: 'PST' }) } catch (e) { e.name }", "RangeError")]
    [InlineData("try { new Intl.DateTimeFormat('en', { dateStyle: 'short', hour: 'numeric' }) } catch (e) { e.name }", "TypeError")]
    [InlineData("new Intl.DateTimeFormat('en', { timeZone: 'UTC', year: 'numeric', era: 'short' }).format(-8.64e15)", "271822 BC")]
    [InlineData("Object.prototype.toString.call(new Intl.DateTimeFormat())", "[object Intl.DateTimeFormat]")]
    public void DateTimeFormat(string src, string expected) => Check(src, expected);

    [Theory]
    [InlineData("new Date(NaN).toLocaleString()", "Invalid Date")]
    [InlineData("new Date(Date.UTC(2020, 1, 2, 3, 4, 5)).toLocaleDateString('en-US', { timeZone: 'UTC' })", "2/2/2020")]
    [InlineData("new Date(Date.UTC(2020, 1, 2, 3, 4, 5)).toLocaleTimeString('en-GB', { timeZone: 'UTC' })", "03:04:05")]
    [InlineData("var d = new Date(Date.UTC(2020, 1, 2, 3, 4, 5)); d.toLocaleString() === new Intl.DateTimeFormat(undefined, { year: 'numeric', month: 'numeric', day: 'numeric', hour: 'numeric', minute: 'numeric', second: 'numeric' }).format(d)", "true")]
    public void DateToLocale(string src, string expected) => Check(src, expected);

    [Theory]
    [InlineData("new Temporal.PlainDate(2021, 8, 4).toLocaleString('en-US')", "8/4/2021")]
    [InlineData("new Temporal.PlainTime(13, 4).toLocaleString('en-GB')", "13:04:00")]
    [InlineData("new Intl.DateTimeFormat('en-US', { month: '2-digit', hour: '2-digit', timeZone: 'UTC' }).format(new Temporal.PlainDate(2021, 8, 4))", "08")]
    [InlineData("try { new Intl.DateTimeFormat('en', { year: 'numeric' }).format(new Temporal.PlainTime(1)) } catch (e) { e.name }", "TypeError")]
    [InlineData("Temporal.Instant.fromEpochMilliseconds(0).toZonedDateTimeISO('Asia/Kolkata').toLocaleString('en-GB', { hour: 'numeric', minute: 'numeric' })", "05:30")]
    [InlineData("try { new Intl.DateTimeFormat('en').format(Temporal.Instant.fromEpochMilliseconds(0).toZonedDateTimeISO('UTC')) } catch (e) { e.name }", "TypeError")]
    public void Temporal(string src, string expected) => Check(src, expected);
}
