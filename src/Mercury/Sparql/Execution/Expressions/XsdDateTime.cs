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
        if (!TryRead(lexical, isDate, out var fields))
            return false;

        // The fraction of a second as ticks: its first seven digits, the rest truncated
        long time = fields.Hour * TimeSpan.TicksPerHour + fields.Minute * TimeSpan.TicksPerMinute
            + (long)decimal.Truncate(fields.Second * TimeSpan.TicksPerSecond);
        long offset = 0;
        if (fields.Zone.Length == 6)
        {
            int zoneHours = (fields.Zone[1] - '0') * 10 + (fields.Zone[2] - '0');
            int zoneMinutes = (fields.Zone[4] - '0') * 10 + (fields.Zone[5] - '0');
            offset = (zoneHours * TimeSpan.TicksPerHour + zoneMinutes * TimeSpan.TicksPerMinute) * (fields.Zone[0] == '-' ? -1 : 1);
        }

        hasTimezone = !fields.Zone.IsEmpty;
        ticksUtc = new DateTime(fields.Year, fields.Month, fields.Day).Ticks + time - offset;
        return true;
    }

    /// <summary>
    /// The fields of a valid <c>xsd:dateTime</c> lexical form <c>yyyy-MM-ddThh:mm:ss(.s+)?(Z|±hh:mm)?</c>, or of an
    /// <c>xsd:date</c> <c>yyyy-MM-dd(Z|±hh:mm)?</c>, as written, except that <c>24:00:00</c> is the next day's
    /// <c>00:00:00</c>: SPARQL's YEAR … TZ read them with no timezone conversion (§17.4.5). The one reader of the
    /// form: <see cref="TryParse"/> computes the instant from these fields.
    /// False when the form is not valid (a month, day, hour, minute, second or timezone out of range included).
    /// </summary>
    public static bool TryRead(ReadOnlySpan<char> lexical, bool isDate, out XsdDateTimeFields fields)
    {
        fields = default;
        if (lexical.Length < 10 || lexical[4] != '-' || lexical[7] != '-'
            || !Digits(lexical[..4], out int year) || !Digits(lexical.Slice(5, 2), out int month)
            || !Digits(lexical.Slice(8, 2), out int day)
            || year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
            return false;

        int at = 10;
        int hour = 0, minute = 0;
        decimal second = 0;
        if (!isDate)
        {
            if (lexical.Length < 19 || lexical[10] != 'T' || lexical[13] != ':' || lexical[16] != ':'
                || !Digits(lexical.Slice(11, 2), out hour) || !Digits(lexical.Slice(14, 2), out minute)
                || !Digits(lexical.Slice(17, 2), out int wholeSeconds) || minute > 59 || wholeSeconds > 59)
                return false;
            at = 19;
            if (at < lexical.Length && lexical[at] == '.')
            {
                int start = ++at;
                while (at < lexical.Length && char.IsAsciiDigit(lexical[at])) at++;
                if (at == start)
                    return false;
            }
            second = decimal.Parse(lexical[17..at], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
            if (hour > 24 || (hour == 24 && (minute != 0 || second != 0)))
                return false;
            if (hour == 24)
            {
                // 24:00:00 is the first instant of the next day (XML Schema 2 §3.2.7)
                if (year == 9999 && month == 12 && day == 31)
                    return false;
                var next = new DateTime(year, month, day).AddDays(1);
                (year, month, day, hour) = (next.Year, next.Month, next.Day, 0);
            }
        }

        var zone = lexical[at..];
        bool zoneValid = zone.IsEmpty || zone is "Z"
            || (zone.Length == 6 && zone[0] is '+' or '-' && zone[3] == ':'
                && Digits(zone.Slice(1, 2), out int zoneHours) && Digits(zone.Slice(4, 2), out int zoneMinutes)
                && zoneHours <= 14 && zoneMinutes <= 59);
        if (!zoneValid)
            return false;

        fields = new XsdDateTimeFields(year, month, day, hour, minute, second, zone);
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

/// <summary>The fields of an <c>xsd:dateTime</c> or <c>xsd:date</c> lexical form, as written (<see cref="XsdDateTime.TryRead"/>).</summary>
internal readonly ref struct XsdDateTimeFields
{
    public readonly int Year;
    public readonly int Month;
    public readonly int Day;
    public readonly int Hour;
    public readonly int Minute;
    /// <summary>The seconds with their fraction, as written.</summary>
    public readonly decimal Second;
    /// <summary>The timezone as written: "Z", "+hh:mm" or "-hh:mm"; empty when there is none.</summary>
    public readonly ReadOnlySpan<char> Zone;

    public XsdDateTimeFields(int year, int month, int day, int hour, int minute, decimal second, ReadOnlySpan<char> zone)
    {
        Year = year;
        Month = month;
        Day = day;
        Hour = hour;
        Minute = minute;
        Second = second;
        Zone = zone;
    }
}
