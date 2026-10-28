using System;
using System.Globalization;

namespace SkyOmega.Mercury.Sparql.Execution.Expressions;

/// <summary>
/// The value of an <c>xsd:dateTime</c> or <c>xsd:date</c> lexical form, as the operator mapping compares them
/// (SPARQL 1.1 §17.3, XPath op:dateTime-equal / op:date-equal): an instant in UTC ticks, and whether it has a timezone.
/// <c>24:00:00</c> is the first instant of the next day. Values with and without a timezone are in XSD's partial order
/// (XML Schema 2 §3.2.7.4): a value without one may be in any timezone within 14 hours of UTC, so it is ordered against
/// a value with one only when they are further apart than that, and incomparable otherwise.
/// </summary>
internal static class XsdDateTime
{
    /// <summary>
    /// The instant <paramref name="lexical"/> names: <c>yyyy-MM-ddThh:mm:ss(.s+)?(Z|±hh:mm)?</c> for a dateTime, or
    /// <c>yyyy-MM-dd(Z|±hh:mm)?</c> for a date (its first instant). False when the form is not valid.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> lexical, bool isDate, out long ticksUtc) =>
        TryParse(lexical, isDate, out ticksUtc, out _);

    /// <summary>
    /// The order of two values (<paramref name="order"/>, as CompareTo gives it); false when they are incomparable: one
    /// has a timezone, the other hasn't, and they are within 14 hours of each other.
    /// </summary>
    public static bool TryCompare(ReadOnlySpan<char> a, ReadOnlySpan<char> b, bool isDate, out int order)
    {
        TryParse(a, isDate, out long x, out bool xZoned);
        TryParse(b, isDate, out long y, out bool yZoned);
        order = x.CompareTo(y);
        if (xZoned == yZoned)
            return true;
        long margin = 14 * TimeSpan.TicksPerHour;
        return Math.Abs(x - y) > margin;
    }

    /// <summary>The instant, and whether the lexical form has a timezone (read as UTC when it hasn't).</summary>
    public static bool TryParse(ReadOnlySpan<char> lexical, bool isDate, out long ticksUtc, out bool hasTimezone)
    {
        ticksUtc = 0;
        hasTimezone = false;
        if (lexical.Length < 10 || lexical[4] != '-' || lexical[7] != '-'
            || !Digits(lexical[..4], out int year) || !Digits(lexical.Slice(5, 2), out int month)
            || !Digits(lexical.Slice(8, 2), out int day)
            || year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
            return false;

        int at = 10;
        long time = 0;
        if (!isDate)
        {
            if (lexical.Length < 19 || lexical[10] != 'T' || lexical[13] != ':' || lexical[16] != ':'
                || !Digits(lexical.Slice(11, 2), out int hour) || !Digits(lexical.Slice(14, 2), out int minute)
                || !Digits(lexical.Slice(17, 2), out int second) || minute > 59 || second > 59)
                return false;
            at = 19;
            long fraction = 0;
            if (at < lexical.Length && lexical[at] == '.')
            {
                int start = ++at;
                while (at < lexical.Length && char.IsAsciiDigit(lexical[at])) at++;
                if (at == start)
                    return false;
                var digits = lexical[start..Math.Min(at, start + 7)];
                fraction = long.Parse(digits, CultureInfo.InvariantCulture);
                for (int i = digits.Length; i < 7; i++) fraction *= 10;
            }
            if (hour > 24 || (hour == 24 && (minute != 0 || second != 0 || fraction != 0)))
                return false;
            time = hour * TimeSpan.TicksPerHour + minute * TimeSpan.TicksPerMinute + second * TimeSpan.TicksPerSecond + fraction;
        }

        long offset = 0;
        var zone = lexical[at..];
        if (zone is "Z")
        {
        }
        else if (zone.Length == 6 && zone[0] is '+' or '-' && zone[3] == ':'
                 && Digits(zone.Slice(1, 2), out int zoneHours) && Digits(zone.Slice(4, 2), out int zoneMinutes)
                 && zoneHours <= 14 && zoneMinutes <= 59)
        {
            offset = (zoneHours * TimeSpan.TicksPerHour + zoneMinutes * TimeSpan.TicksPerMinute) * (zone[0] == '-' ? -1 : 1);
        }
        else if (!zone.IsEmpty)
        {
            return false;
        }

        hasTimezone = !zone.IsEmpty;
        ticksUtc = new DateTime(year, month, day).Ticks + time - offset;
        return true;
    }

    private static bool Digits(ReadOnlySpan<char> s, out int value)
    {
        value = 0;
        foreach (var c in s)
        {
            if (!char.IsAsciiDigit(c))
                return false;
            value = value * 10 + (c - '0');
        }
        return true;
    }
}
