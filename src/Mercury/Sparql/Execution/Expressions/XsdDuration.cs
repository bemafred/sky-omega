using System;
using System.Globalization;
using SkyOmega.Mercury.Rdf;

namespace SkyOmega.Mercury.Sparql.Execution.Expressions;

/// <summary>
/// The value of an <c>xsd:duration</c>, <c>xsd:dayTimeDuration</c> or <c>xsd:yearMonthDuration</c> lexical form
/// (XML Schema 1.1 Part 2 §3.3.6, §3.4.26, §3.4.27): a number of months and a number of seconds, held as ticks, with
/// the form's one sign. A fraction of a second past seven digits is truncated, as <see cref="XsdDateTime"/> truncates it.
/// </summary>
internal static class XsdDuration
{
    /// <summary>
    /// The months and ticks of <paramref name="lexical"/>, a form of the duration datatype <paramref name="kind"/>:
    /// <c>-?P(nY)?(nM)?(nD)?(T(nH)?(nM)?(n(.n+)?S)?)?</c> with at least one part, and at least one after a <c>T</c>.
    /// A dayTimeDuration has no years or months, a yearMonthDuration no days or time. False when the form is not one
    /// of that datatype, or its value does not fit.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> lexical, XsdKind kind, out long months, out long ticks)
    {
        months = 0;
        ticks = 0;
        int at = 0;
        bool negative = lexical.Length > 0 && lexical[0] == '-';
        if (negative)
            at++;
        if (at >= lexical.Length || lexical[at] != 'P')
            return false;
        at++;

        // Each designator at most once and in this order; the last three only after the T
        const string Designators = "YMDHMS";
        const int FirstTimeDesignator = 3;
        int next = 0;
        bool afterT = false, hasYearOrMonth = false, hasDays = false, hasTimePart = false;
        long years = 0, monthCount = 0, days = 0, hours = 0, minutes = 0;
        decimal seconds = 0;
        while (at < lexical.Length)
        {
            if (lexical[at] == 'T')
            {
                if (afterT)
                    return false;
                afterT = true;
                next = FirstTimeDesignator;
                at++;
                continue;
            }

            int start = at;
            while (at < lexical.Length && char.IsAsciiDigit(lexical[at]))
                at++;
            if (at == start)
                return false;
            int wholeEnd = at;
            bool hasFraction = false;
            if (at < lexical.Length && lexical[at] == '.')
            {
                int fractionStart = ++at;
                while (at < lexical.Length && char.IsAsciiDigit(lexical[at]))
                    at++;
                if (at == fractionStart)
                    return false;
                hasFraction = true;
            }
            if (at >= lexical.Length)
                return false;

            int designator = Designators.IndexOf(lexical[at], afterT ? FirstTimeDesignator : 0);
            if (designator < next || (!afterT && designator >= FirstTimeDesignator) || (hasFraction && designator != 5))
                return false;
            next = designator + 1;

            if (designator == 5)
            {
                if (!decimal.TryParse(lexical[start..at], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out seconds))
                    return false;
            }
            else
            {
                if (!long.TryParse(lexical[start..wholeEnd], NumberStyles.None, CultureInfo.InvariantCulture, out long number))
                    return false;
                switch (designator)
                {
                    case 0: years = number; break;
                    case 1: monthCount = number; break;
                    case 2: days = number; break;
                    case 3: hours = number; break;
                    default: minutes = number; break;
                }
            }
            hasYearOrMonth |= designator < 2;
            hasDays |= designator == 2;
            hasTimePart |= designator >= FirstTimeDesignator;
            at++;
        }

        if (!(hasYearOrMonth || hasDays || hasTimePart) || (afterT && !hasTimePart))
            return false;
        if (kind == XsdKind.DayTimeDuration && hasYearOrMonth)
            return false;
        if (kind == XsdKind.YearMonthDuration && (hasDays || afterT))
            return false;

        try
        {
            months = checked(years * 12 + monthCount);
            ticks = checked(days * TimeSpan.TicksPerDay + hours * TimeSpan.TicksPerHour + minutes * TimeSpan.TicksPerMinute
                + (long)decimal.Truncate(seconds * TimeSpan.TicksPerSecond));
        }
        catch (OverflowException)
        {
            return false;
        }
        if (negative)
        {
            months = -months;
            ticks = -ticks;
        }
        return true;
    }

    /// <summary>
    /// The canonical form of the dayTimeDuration of <paramref name="ticks"/> (XML Schema 1.1 Part 2 §3.4.27.2): the
    /// days, then the hours below 24 and the minutes and seconds below 60, each left out when it is 0; <c>PT0S</c> when
    /// all are.
    /// </summary>
    public static string DayTime(long ticks)
    {
        if (ticks == 0)
            return "PT0S";
        ulong magnitude = ticks < 0 ? (ulong)(-(ticks + 1)) + 1 : (ulong)ticks;
        ulong days = magnitude / TimeSpan.TicksPerDay;
        ulong rest = magnitude % TimeSpan.TicksPerDay;
        ulong hours = rest / TimeSpan.TicksPerHour;
        rest %= TimeSpan.TicksPerHour;
        ulong minutes = rest / TimeSpan.TicksPerMinute;
        rest %= TimeSpan.TicksPerMinute;
        ulong seconds = rest / TimeSpan.TicksPerSecond;
        ulong fraction = rest % TimeSpan.TicksPerSecond;

        var text = new System.Text.StringBuilder(32);
        if (ticks < 0)
            text.Append('-');
        text.Append('P');
        if (days != 0)
            text.Append(days.ToString(CultureInfo.InvariantCulture)).Append('D');
        if (hours != 0 || minutes != 0 || seconds != 0 || fraction != 0)
        {
            text.Append('T');
            if (hours != 0)
                text.Append(hours.ToString(CultureInfo.InvariantCulture)).Append('H');
            if (minutes != 0)
                text.Append(minutes.ToString(CultureInfo.InvariantCulture)).Append('M');
            if (seconds != 0 || fraction != 0)
            {
                text.Append(seconds.ToString(CultureInfo.InvariantCulture));
                if (fraction != 0)
                    text.Append('.').Append(fraction.ToString("D7", CultureInfo.InvariantCulture).TrimEnd('0'));
                text.Append('S');
            }
        }
        return text.ToString();
    }
}
