using System;
using System.IO;
using SkyOmega.Mercury.Diagnostics;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Diagnostics;

/// <summary>
/// The log Mercury owns (ADR-061 M7): <see cref="FileLogger"/> appends what a store reports to a file, so that it
/// can be read after a failure, and a pool hands its logger to every store it opens.
/// </summary>
public class FileLoggerTests : IDisposable
{
    private readonly string _testDir;

    public FileLoggerTests()
    {
        var tempPath = TempPath.Test("file-logger");
        tempPath.MarkOwnership();
        _testDir = tempPath;
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose() => TempPath.SafeCleanup(_testDir);

    [Fact]
    public void AMessage_IsALineWithItsTimeInUtcAndItsLevel()
    {
        var path = Path.Combine(_testDir, "mercury.log");
        var clock = new SuppliedClock { Reading = new DateTimeOffset(2026, 11, 14, 19, 40, 7, 615, TimeSpan.FromHours(2)) };

        using (var log = new FileLogger(path, LogLevel.Info, clock: clock))
        {
            log.Log(LogLevel.Info, "Opening store at {0}", "/stores/a");
            clock.Reading = clock.Reading.AddSeconds(1);
            log.Log(LogLevel.Critical, "Internal error in the Mercury store");
            Assert.Equal(0, log.LinesNotWritten);
        }

        Assert.Equal(
            [
                "2026-11-14T17:40:07.615Z INF Opening store at /stores/a",
                "2026-11-14T17:40:08.615Z CRT Internal error in the Mercury store"
            ],
            File.ReadAllLines(path));
    }

    [Fact]
    public void ALoggerOpenedAgain_AddsToTheFile()
    {
        var path = Path.Combine(_testDir, "mercury.log");
        var clock = new SuppliedClock { Reading = new DateTimeOffset(2026, 11, 14, 8, 0, 0, TimeSpan.Zero) };

        using (var first = new FileLogger(path, clock: clock))
            first.Log(LogLevel.Warning, "first run");
        using (var second = new FileLogger(path, clock: clock))
            second.Log(LogLevel.Warning, "second run");

        Assert.Equal(
            ["2026-11-14T08:00:00.000Z WRN first run", "2026-11-14T08:00:00.000Z WRN second run"],
            File.ReadAllLines(path));
    }

    [Fact]
    public void AFirstLine_IsWrittenOnceBeforeTheFirstMessage_AndNotAtAllWhenNothingIsLogged()
    {
        var path = Path.Combine(_testDir, "mercury.log");
        var clock = new SuppliedClock { Reading = new DateTimeOffset(2026, 11, 14, 8, 0, 0, TimeSpan.Zero) };

        // A run that logs nothing leaves nothing: a tool that did not get the store writes no line.
        using (var silent = new FileLogger(path, LogLevel.Warning, clock: clock, firstLine: "mercury 1.0 starts, process 1"))
            silent.Log(LogLevel.Info, "below the minimum level");
        Assert.False(File.Exists(path));

        using (var log = new FileLogger(path, LogLevel.Warning, clock: clock, firstLine: "mercury 1.0 starts, process 2"))
        {
            log.Log(LogLevel.Warning, "first message");
            log.Log(LogLevel.Error, "second message");
        }

        Assert.Equal(
            [
                "2026-11-14T08:00:00.000Z INF mercury 1.0 starts, process 2",
                "2026-11-14T08:00:00.000Z WRN first message",
                "2026-11-14T08:00:00.000Z ERR second message"
            ],
            File.ReadAllLines(path));
    }

    [Fact]
    public void AMessageBelowTheMinimumLevel_IsNotWritten_AndALoggerThatWroteNothingMakesNoFile()
    {
        var path = Path.Combine(_testDir, "mercury.log");

        using (var log = new FileLogger(path, LogLevel.Warning))
        {
            Assert.False(log.IsEnabled(LogLevel.Info));
            Assert.True(log.IsEnabled(LogLevel.Warning));
            Assert.False(log.IsEnabled(LogLevel.None));
            log.Log(LogLevel.Info, "not for this log");
            log.Log(LogLevel.Debug, "nor this, {0}", 1);
        }

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void AFileThatALineWouldTakePastItsLimit_IsPutAside_AndTwoFilesAreKeptAtMost()
    {
        var path = Path.Combine(_testDir, "logs", "mercury.log");
        var clock = new SuppliedClock { Reading = new DateTimeOffset(2026, 11, 14, 8, 0, 0, TimeSpan.Zero) };

        // A line is 24 characters of time, a space, 3 of level, a space, the message and the line end. With a
        // message of 70 characters three lines do not fit in 250 bytes, and two do.
        var message = new string('m', 70);
        using (var log = new FileLogger(path, LogLevel.Info, maximumBytes: 250, clock: clock))
        {
            for (int line = 1; line <= 7; line++)
                log.Log(LogLevel.Info, $"{line} {message}");
            Assert.Equal(0, log.LinesNotWritten);
        }

        // Lines 1 and 2 were put aside and then replaced by 3 and 4, and those by 5 and 6. Line 7 started a new file.
        var current = File.ReadAllLines(path);
        var putAside = File.ReadAllLines(path + ".1");
        Assert.EndsWith($"7 {message}", Assert.Single(current));
        Assert.Equal(2, putAside.Length);
        Assert.EndsWith($"5 {message}", putAside[0]);
        Assert.EndsWith($"6 {message}", putAside[1]);
        Assert.True(new FileInfo(path + ".1").Length <= 250);
        Assert.Equal(["mercury.log", "mercury.log.1"], Array.ConvertAll(Directory.GetFiles(Path.Combine(_testDir, "logs")), Path.GetFileName).Order());
    }

    [Fact]
    public void ALineThatCannotBeWritten_IsCounted_AndTheCallerIsNotDisturbed()
    {
        // The log's directory cannot be made: a file has its name.
        var inTheWay = Path.Combine(_testDir, "not-a-directory");
        File.WriteAllText(inTheWay, "a file");

        using var log = new FileLogger(Path.Combine(inTheWay, "mercury.log"));
        log.Log(LogLevel.Critical, "a failure the log cannot record");
        log.Log(LogLevel.Error, "and another");

        Assert.Equal(2, log.LinesNotWritten);
    }

    [Fact]
    public void AMessageLoggedAfterTheLoggerIsClosed_IsNotWritten()
    {
        var path = Path.Combine(_testDir, "mercury.log");
        var log = new FileLogger(path);
        log.Log(LogLevel.Warning, "while open");
        log.Dispose();
        log.Log(LogLevel.Warning, "after it was closed");

        Assert.EndsWith("WRN while open", Assert.Single(File.ReadAllLines(path)));
    }

    [Fact]
    public void AStoreOpenedByAPool_LogsToThePoolsLogger()
    {
        var poolPath = Path.Combine(_testDir, "pool");
        var logPath = Path.Combine(poolPath, "mercury.log");

        using (var log = new FileLogger(logPath))
        {
            using (var pool = new QuadStorePool(poolPath, new QuadStorePoolOptions { StorageOptions = StorageOptions.ForTesting, Logger = log }))
            {
                pool.EnsureActive("primary");
                pool.Active.AddCurrent("<http://ex.org/s>", "<http://ex.org/p>", "\"o\"");

                // A store the pool lends out is opened by the pool too.
                pool.Return(pool.Rent());
            }

            // The same pool opened again finds its store and opens it, with the same logger.
            using (var pool = new QuadStorePool(poolPath, new QuadStorePoolOptions { StorageOptions = StorageOptions.ForTesting, Logger = log }))
                Assert.Equal(1, pool.Active.GetStatistics().QuadCount);
        }

        var opened = Array.FindAll(File.ReadAllLines(logPath), line => line.Contains(" INF Opening store at "));
        Assert.Equal(3, opened.Length);
        Assert.Contains(Path.Combine(poolPath, "stores"), opened[0]);
        Assert.Contains(Path.Combine(poolPath, "pooled"), opened[1]);
        Assert.Contains(Path.Combine(poolPath, "stores"), opened[2]);
    }
}
