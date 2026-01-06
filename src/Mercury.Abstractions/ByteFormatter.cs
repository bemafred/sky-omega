// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

namespace SkyOmega.Mercury.Abstractions;

/// <summary>
/// Culture-invariant byte size formatting utility.
/// Always uses decimal point (.) regardless of system locale.
/// </summary>
public static class ByteFormatter
{
    /// <summary>
    /// Formats a byte count as a human-readable string with appropriate unit.
    /// Uses two decimal places (e.g., "1.00 GB", "500.00 MB").
    /// </summary>
    /// <param name="bytes">The byte count to format.</param>
    /// <returns>A culture-invariant formatted string.</returns>
    public static string Format(long bytes)
    {
        if (bytes < 0) bytes = 0;
        return bytes switch
        {
            >= 1L << 40 => (bytes / (double)(1L << 40)).ToString("F2", CultureInfo.InvariantCulture) + " TB",
            >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("F2", CultureInfo.InvariantCulture) + " GB",
            >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("F2", CultureInfo.InvariantCulture) + " MB",
            >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("F2", CultureInfo.InvariantCulture) + " KB",
            _ => $"{bytes} bytes"
        };
    }

    /// <summary>
    /// Formats a byte count with one decimal place for larger units (e.g., "1.5 GB", "500 B").
    /// Uses N0 for bytes, N1 for larger units.
    /// </summary>
    /// <param name="bytes">The byte count to format.</param>
    /// <returns>A culture-invariant formatted string.</returns>
    public static string FormatCompact(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? size.ToString("N0", CultureInfo.InvariantCulture) + " " + units[unit]
            : size.ToString("N1", CultureInfo.InvariantCulture) + " " + units[unit];
    }
}
