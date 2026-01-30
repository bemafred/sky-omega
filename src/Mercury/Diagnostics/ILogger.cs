// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;

namespace SkyOmega.Mercury.Diagnostics;

/// <summary>
/// Log level for runtime logging, ordered from most verbose to least.
/// </summary>
public enum LogLevel : byte
{
    /// <summary>Detailed tracing information for debugging.</summary>
    Trace = 0,
    /// <summary>Debug information useful during development.</summary>
    Debug = 1,
    /// <summary>General informational messages.</summary>
    Info = 2,
    /// <summary>Potential issues that don't prevent operation.</summary>
    Warning = 3,
    /// <summary>Errors that may affect functionality.</summary>
    Error = 4,
    /// <summary>Critical failures requiring immediate attention.</summary>
    Critical = 5,
    /// <summary>Logging disabled.</summary>
    None = 6
}

/// <summary>
/// Zero-allocation logging abstraction for Mercury components.
/// </summary>
/// <remarks>
/// <para>
/// Design goals:
/// - BCL only, no external dependencies
/// - Zero allocations on hot paths when logging is disabled
/// - Span-based API for zero-copy message passing
/// - Simple interface for easy implementation
/// </para>
/// <para>
/// Usage pattern:
/// <code>
/// if (_logger.IsEnabled(LogLevel.Debug))
///     _logger.Log(LogLevel.Debug, "Processing query");
/// </code>
/// </para>
/// </remarks>
public interface ILogger
{
    /// <summary>
    /// Checks if a given log level is enabled.
    /// </summary>
    /// <remarks>
    /// Always check before formatting expensive messages to avoid allocations.
    /// </remarks>
    bool IsEnabled(LogLevel level);

    /// <summary>
    /// Logs a simple message at the specified level.
    /// </summary>
    void Log(LogLevel level, ReadOnlySpan<char> message);

    /// <summary>
    /// Logs a message with a single argument.
    /// </summary>
    void Log<T>(LogLevel level, ReadOnlySpan<char> message, T arg);

    /// <summary>
    /// Logs a message with two arguments.
    /// </summary>
    void Log<T1, T2>(LogLevel level, ReadOnlySpan<char> message, T1 arg1, T2 arg2);

    /// <summary>
    /// Logs a message with three arguments.
    /// </summary>
    void Log<T1, T2, T3>(LogLevel level, ReadOnlySpan<char> message, T1 arg1, T2 arg2, T3 arg3);
}

/// <summary>
/// A no-op logger that discards all messages with zero overhead.
/// </summary>
/// <remarks>
/// Use <see cref="Instance"/> for production deployments where logging is not needed.
/// All methods are aggressively inlined to eliminate call overhead.
/// </remarks>
public sealed class NullLogger : ILogger
{
    /// <summary>
    /// Singleton instance of the null logger.
    /// </summary>
    public static readonly NullLogger Instance = new();

    private NullLogger() { }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsEnabled(LogLevel level) => false;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Log(LogLevel level, ReadOnlySpan<char> message) { }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Log<T>(LogLevel level, ReadOnlySpan<char> message, T arg) { }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Log<T1, T2>(LogLevel level, ReadOnlySpan<char> message, T1 arg1, T2 arg2) { }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Log<T1, T2, T3>(LogLevel level, ReadOnlySpan<char> message, T1 arg1, T2 arg2, T3 arg3) { }
}

/// <summary>
/// A simple console logger for development and debugging.
/// </summary>
/// <remarks>
/// Output format: [HH:mm:ss.fff] [LEVEL] message
/// This logger allocates when formatting messages - use <see cref="NullLogger"/> in production.
/// </remarks>
public sealed class ConsoleLogger : ILogger
{
    private readonly LogLevel _minLevel;
    private readonly object _lock = new();

    /// <summary>
    /// Creates a console logger with the specified minimum log level.
    /// </summary>
    /// <param name="minLevel">Minimum level to log. Messages below this level are discarded.</param>
    public ConsoleLogger(LogLevel minLevel = LogLevel.Info)
    {
        _minLevel = minLevel;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsEnabled(LogLevel level) => level >= _minLevel && level != LogLevel.None;

    /// <inheritdoc/>
    public void Log(LogLevel level, ReadOnlySpan<char> message)
    {
        if (!IsEnabled(level)) return;
        WriteLog(level, message.ToString());
    }

    /// <inheritdoc/>
    public void Log<T>(LogLevel level, ReadOnlySpan<char> message, T arg)
    {
        if (!IsEnabled(level)) return;
        WriteLog(level, string.Format(message.ToString(), arg));
    }

    /// <inheritdoc/>
    public void Log<T1, T2>(LogLevel level, ReadOnlySpan<char> message, T1 arg1, T2 arg2)
    {
        if (!IsEnabled(level)) return;
        WriteLog(level, string.Format(message.ToString(), arg1, arg2));
    }

    /// <inheritdoc/>
    public void Log<T1, T2, T3>(LogLevel level, ReadOnlySpan<char> message, T1 arg1, T2 arg2, T3 arg3)
    {
        if (!IsEnabled(level)) return;
        WriteLog(level, string.Format(message.ToString(), arg1, arg2, arg3));
    }

    private void WriteLog(LogLevel level, string formattedMessage)
    {
        var timestamp = DateTime.UtcNow.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
        var levelStr = level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Info => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???"
        };

        var color = level switch
        {
            LogLevel.Warning => ConsoleColor.Yellow,
            LogLevel.Error => ConsoleColor.Red,
            LogLevel.Critical => ConsoleColor.Magenta,
            _ => Console.ForegroundColor
        };

        lock (_lock)
        {
            var originalColor = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{timestamp}] ");
            Console.ForegroundColor = color;
            Console.Write($"[{levelStr}] ");
            Console.ForegroundColor = originalColor;
            Console.WriteLine(formattedMessage);
        }
    }
}

/// <summary>
/// Extension methods for <see cref="ILogger"/>.
/// </summary>
public static class LoggerExtensions
{
    /// <summary>Logs a trace message.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Trace(this ILogger logger, ReadOnlySpan<char> message)
    {
        if (logger.IsEnabled(LogLevel.Trace))
            logger.Log(LogLevel.Trace, message);
    }

    /// <summary>Logs a debug message.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Debug(this ILogger logger, ReadOnlySpan<char> message)
    {
        if (logger.IsEnabled(LogLevel.Debug))
            logger.Log(LogLevel.Debug, message);
    }

    /// <summary>Logs an info message.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Info(this ILogger logger, ReadOnlySpan<char> message)
    {
        if (logger.IsEnabled(LogLevel.Info))
            logger.Log(LogLevel.Info, message);
    }

    /// <summary>Logs a warning message.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Warning(this ILogger logger, ReadOnlySpan<char> message)
    {
        if (logger.IsEnabled(LogLevel.Warning))
            logger.Log(LogLevel.Warning, message);
    }

    /// <summary>Logs an error message.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Error(this ILogger logger, ReadOnlySpan<char> message)
    {
        if (logger.IsEnabled(LogLevel.Error))
            logger.Log(LogLevel.Error, message);
    }

    /// <summary>Logs a critical message.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Critical(this ILogger logger, ReadOnlySpan<char> message)
    {
        if (logger.IsEnabled(LogLevel.Critical))
            logger.Log(LogLevel.Critical, message);
    }

    /// <summary>Logs a trace message with one argument.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Trace<T>(this ILogger logger, ReadOnlySpan<char> message, T arg)
    {
        if (logger.IsEnabled(LogLevel.Trace))
            logger.Log(LogLevel.Trace, message, arg);
    }

    /// <summary>Logs a debug message with one argument.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Debug<T>(this ILogger logger, ReadOnlySpan<char> message, T arg)
    {
        if (logger.IsEnabled(LogLevel.Debug))
            logger.Log(LogLevel.Debug, message, arg);
    }

    /// <summary>Logs an info message with one argument.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Info<T>(this ILogger logger, ReadOnlySpan<char> message, T arg)
    {
        if (logger.IsEnabled(LogLevel.Info))
            logger.Log(LogLevel.Info, message, arg);
    }

    /// <summary>Logs a warning message with one argument.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Warning<T>(this ILogger logger, ReadOnlySpan<char> message, T arg)
    {
        if (logger.IsEnabled(LogLevel.Warning))
            logger.Log(LogLevel.Warning, message, arg);
    }

    /// <summary>Logs an error message with one argument.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Error<T>(this ILogger logger, ReadOnlySpan<char> message, T arg)
    {
        if (logger.IsEnabled(LogLevel.Error))
            logger.Log(LogLevel.Error, message, arg);
    }
}
