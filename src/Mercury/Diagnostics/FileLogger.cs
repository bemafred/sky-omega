// FileLogger.cs
// A logger that appends lines to a file, so that what a store reported can be read after a failure.
// No external dependencies, only BCL.
// .NET 10 / C# 14

using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace SkyOmega.Mercury.Diagnostics;

/// <summary>
/// Appends each message as a line to a file. It is the log a person or an agent reads after a failure: the
/// process that met the failure may be gone, and what it wrote to its standard error with it.
/// </summary>
/// <remarks>
/// <para>
/// A line is <c>2026-11-14T17:40:07.615Z CRT message</c>: the time in UTC, the level, the message. A message of
/// several lines (an exception with its stack) is written as it is, so its later lines carry no time.
/// </para>
/// <para>
/// The file is bounded. When a line would take it past <c>maximumBytes</c>, the file is renamed to the same name
/// with <c>.1</c> added, replacing the one before it, and a new file is started. Two files are kept at most.
/// </para>
/// <para>
/// A message at <see cref="LogLevel.Error"/> or above is on disk when <see cref="Log(LogLevel, ReadOnlySpan{char})"/>
/// returns. A message below that is handed to the operating system, which is enough when the process dies and not
/// when the machine loses power.
/// </para>
/// <para>
/// The file is opened when the first message is written, not before: a tool makes its logger before it knows that
/// it may use the store, and a second process must not disturb the log of the one that holds the store. A logger
/// may be given a first line, which says who writes (the tool, its version, its process). It is written before
/// the first message, so each run starts with it, and a run that logs nothing writes nothing.
/// </para>
/// <para>
/// A line that cannot be written is dropped and counted in <see cref="LinesNotWritten"/>: a logger must not turn
/// the failure it is asked to record into another one. A tool reads the count once its store is open, so that a
/// log that cannot be written is noticed at once.
/// </para>
/// <para>
/// The times are the system clock's, or those of the clock given. They are diagnostics, and no part of what a
/// store holds.
/// </para>
/// </remarks>
public sealed class FileLogger : ILogger, IDisposable
{
    /// <summary>The size at which the file is put aside and a new one started: 4 MB.</summary>
    public const long DefaultMaximumBytes = 4L << 20;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _path;
    private readonly LogLevel _minimumLevel;
    private readonly long _maximumBytes;
    private readonly TimeProvider _clock;
    private readonly string? _firstLine;
    private bool _firstLineIsWritten;
    private readonly Lock _writing = new();
    private FileStream? _file;
    private bool _disposed;
    private long _linesNotWritten;

    /// <summary>Path of the file.</summary>
    public string Path => _path;

    /// <summary>How many lines could not be written since the logger was made.</summary>
    public long LinesNotWritten => Interlocked.Read(ref _linesNotWritten);

    /// <param name="path">The file. It is made, with its directory, when the first line is written; lines are added at its end.</param>
    /// <param name="minimumLevel">Messages below this level are not written.</param>
    /// <param name="maximumBytes">The size at which the file is put aside and a new one started.</param>
    /// <param name="clock">Gives the time of each line. Default: the system clock.</param>
    /// <param name="firstLine">Written once, at <see cref="LogLevel.Info"/>, before the first message this logger writes.</param>
    public FileLogger(string path, LogLevel minimumLevel = LogLevel.Info, long maximumBytes = DefaultMaximumBytes,
        TimeProvider? clock = null, string? firstLine = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

        _path = System.IO.Path.GetFullPath(path);
        _minimumLevel = minimumLevel;
        _maximumBytes = maximumBytes;
        _clock = clock ?? TimeProvider.System;
        _firstLine = firstLine;
    }

    private FileStream OpenAtEnd()
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        return new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 4096, FileOptions.None);
    }

    /// <inheritdoc/>
    public bool IsEnabled(LogLevel level) => level >= _minimumLevel && level != LogLevel.None;

    /// <inheritdoc/>
    public void Log(LogLevel level, ReadOnlySpan<char> message)
    {
        if (!IsEnabled(level)) return;
        WriteLine(level, message.ToString());
    }

    /// <inheritdoc/>
    public void Log<T>(LogLevel level, ReadOnlySpan<char> message, T arg)
    {
        if (!IsEnabled(level)) return;
        WriteLine(level, string.Format(CultureInfo.InvariantCulture, message.ToString(), arg));
    }

    /// <inheritdoc/>
    public void Log<T1, T2>(LogLevel level, ReadOnlySpan<char> message, T1 arg1, T2 arg2)
    {
        if (!IsEnabled(level)) return;
        WriteLine(level, string.Format(CultureInfo.InvariantCulture, message.ToString(), arg1, arg2));
    }

    /// <inheritdoc/>
    public void Log<T1, T2, T3>(LogLevel level, ReadOnlySpan<char> message, T1 arg1, T2 arg2, T3 arg3)
    {
        if (!IsEnabled(level)) return;
        WriteLine(level, string.Format(CultureInfo.InvariantCulture, message.ToString(), arg1, arg2, arg3));
    }

    private void WriteLine(LogLevel level, string message)
    {
        var time = _clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var name = level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Info => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            _ => "CRT"
        };

        lock (_writing)
        {
            if (_disposed) return;
            var text = $"{time} {name} {message}{Environment.NewLine}";
            if (_firstLine is not null && !_firstLineIsWritten)
                text = $"{time} INF {_firstLine}{Environment.NewLine}{text}";
            var line = Utf8.GetBytes(text);
            try
            {
                _file ??= OpenAtEnd();
                if (_file.Length > 0 && _file.Length + line.Length > _maximumBytes)
                {
                    _file.Dispose();
                    _file = null;
                    File.Move(_path, _path + ".1", overwrite: true);
                    _file = OpenAtEnd();
                }
                _file.Write(line);
                _file.Flush(flushToDisk: level >= LogLevel.Error);
                _firstLineIsWritten = true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The next line opens the file again.
                Interlocked.Increment(ref _linesNotWritten);
                _file?.Dispose();
                _file = null;
            }
        }
    }

    /// <summary>Closes the file. Messages logged afterwards are not written.</summary>
    public void Dispose()
    {
        lock (_writing)
        {
            _disposed = true;
            _file?.Dispose();
            _file = null;
        }
    }
}
