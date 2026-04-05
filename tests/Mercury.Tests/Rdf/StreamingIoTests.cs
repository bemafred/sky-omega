using System.IO;
using System.IO.Compression;
using System.Text;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Rdf;

/// <summary>
/// Tests for streaming I/O — LoadFileAsync without MemoryStream,
/// chunked batch commits, progress reporting, GZip decompression,
/// compression-aware format detection, and ConvertAsync.
/// </summary>
public class StreamingIoTests : IDisposable
{
    private readonly string _storePath;
    private readonly string _tempDir;
    private QuadStore? _store;

    public StreamingIoTests()
    {
        var tempPath = TempPath.Test("streaming-io");
        tempPath.MarkOwnership();
        _storePath = tempPath;
        _tempDir = Path.Combine(Path.GetTempPath(), $"streaming-io-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        _store?.Dispose();
        TempPath.SafeCleanup(_storePath);
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private QuadStore CreateStore(bool bulkMode = false)
    {
        _store?.Dispose();
        var options = new StorageOptions
        {
            BulkMode = bulkMode,
            IndexInitialSizeBytes = 64L << 20,
            AtomDataInitialSizeBytes = 64L << 20,
            AtomOffsetInitialCapacity = 64L << 10,
            MinimumFreeDiskSpace = 512L << 20
        };
        _store = new QuadStore(_storePath, null, null, options);
        return _store;
    }

    private string WriteTurtleFile(string name, int tripleCount)
    {
        var path = Path.Combine(_tempDir, name);
        using var writer = new StreamWriter(path);
        writer.WriteLine("@prefix ex: <http://example.org/> .");
        for (int i = 0; i < tripleCount; i++)
            writer.WriteLine($"ex:s{i} ex:p \"value-{i}\" .");
        return path;
    }

    private string WriteNTriplesFile(string name, int tripleCount)
    {
        var path = Path.Combine(_tempDir, name);
        using var writer = new StreamWriter(path);
        for (int i = 0; i < tripleCount; i++)
            writer.WriteLine($"<http://example.org/s{i}> <http://example.org/p> \"value-{i}\" .");
        return path;
    }

    #region LoadFileAsync Streaming

    [Fact]
    public async Task LoadFileAsync_Turtle_StreamsFromDisk()
    {
        var store = CreateStore();
        var path = WriteTurtleFile("test.ttl", 100);

        var count = await RdfEngine.LoadFileAsync(store, path);

        Assert.Equal(100, count);
    }

    [Fact]
    public async Task LoadFileAsync_NTriples_StreamsFromDisk()
    {
        var store = CreateStore();
        var path = WriteNTriplesFile("test.nt", 200);

        var count = await RdfEngine.LoadFileAsync(store, path);

        Assert.Equal(200, count);
    }

    [Fact]
    public async Task LoadFileAsync_BulkMode_StreamsWithoutFsync()
    {
        var store = CreateStore(bulkMode: true);
        var path = WriteTurtleFile("bulk.ttl", 500);

        var count = await RdfEngine.LoadFileAsync(store, path);

        Assert.Equal(500, count);
    }

    [Fact]
    public async Task LoadFileAsync_ChunkedCommits_AllTriplesQueryable()
    {
        var store = CreateStore();
        var path = WriteTurtleFile("chunked.ttl", 1000);

        // Use small chunk size to force multiple commits
        var count = await RdfEngine.LoadFileAsync(store, path, chunkSize: 100);

        Assert.Equal(1000, count);

        // Verify data is queryable — spot-check first and last triple
        var q1 = SparqlEngine.Query(store, "ASK { <http://example.org/s0> <http://example.org/p> \"value-0\" }");
        Assert.True(q1.AskResult);
        var q2 = SparqlEngine.Query(store, "ASK { <http://example.org/s999> <http://example.org/p> \"value-999\" }");
        Assert.True(q2.AskResult);
    }

    [Fact]
    public async Task LoadFileAsync_ProgressReporting_FiresAtChunkBoundaries()
    {
        var store = CreateStore();
        var path = WriteTurtleFile("progress.ttl", 500);
        var progressCount = 0;
        long lastTripleCount = 0;

        var count = await RdfEngine.LoadFileAsync(store, path, chunkSize: 100,
            onProgress: p =>
            {
                progressCount++;
                Assert.True(p.TriplesLoaded > lastTripleCount);
                Assert.True(p.TriplesPerSecond > 0);
                lastTripleCount = p.TriplesLoaded;
            });

        Assert.Equal(500, count);
        Assert.True(progressCount >= 5, $"Expected at least 5 progress callbacks, got {progressCount}");
    }

    #endregion

    #region GZip Decompression

    [Fact]
    public async Task LoadFileAsync_GZip_TransparentDecompression()
    {
        var store = CreateStore();

        // Write a .nt file, then gzip it
        var ntPath = WriteNTriplesFile("compressed.nt", 50);
        var gzPath = ntPath + ".gz";

        await using (var input = File.OpenRead(ntPath))
        await using (var output = File.Create(gzPath))
        await using (var gz = new GZipStream(output, CompressionLevel.Fastest))
        {
            await input.CopyToAsync(gz);
        }

        var count = await RdfEngine.LoadFileAsync(store, gzPath);

        Assert.Equal(50, count);
    }

    #endregion

    #region Format Detection

    [Theory]
    [InlineData("data.ttl", RdfFormat.Turtle, false)]
    [InlineData("data.nt", RdfFormat.NTriples, false)]
    [InlineData("data.ttl.gz", RdfFormat.Turtle, true)]
    [InlineData("data.nt.gz", RdfFormat.NTriples, true)]
    [InlineData("data.ttl.bz2", RdfFormat.Turtle, true)]
    [InlineData("data.rdf.gz", RdfFormat.RdfXml, true)]
    [InlineData("data.xyz", RdfFormat.Unknown, false)]
    public void FormatDetection_WithCompression_DetectsCorrectly(string path, RdfFormat expectedFormat, bool isCompressed)
    {
        var (format, compression) = Mercury.Rdf.RdfFormatNegotiator.FromPathStrippingCompression(path.AsSpan());
        Assert.Equal(expectedFormat, format);
        if (isCompressed)
            Assert.NotEqual(Mercury.Rdf.CompressionType.None, compression);
        else
            Assert.Equal(Mercury.Rdf.CompressionType.None, compression);
    }

    #endregion

    #region ConvertAsync

    [Fact]
    public async Task ConvertAsync_TurtleToNTriples_ProducesValidOutput()
    {
        var inputPath = WriteTurtleFile("convert-in.ttl", 100);
        var outputPath = Path.Combine(_tempDir, "convert-out.nt");

        var count = await RdfEngine.ConvertAsync(inputPath, outputPath);

        Assert.Equal(100, count);
        Assert.True(File.Exists(outputPath));

        // Verify the output is valid N-Triples by loading it
        var store = CreateStore();
        var loadCount = await RdfEngine.LoadFileAsync(store, outputPath);
        Assert.Equal(100, loadCount);
    }

    [Fact]
    public async Task ConvertAsync_ProgressReporting_Fires()
    {
        var inputPath = WriteTurtleFile("progress-convert.ttl", 100);
        var outputPath = Path.Combine(_tempDir, "progress-out.nt");
        var progressFired = false;

        await RdfEngine.ConvertAsync(inputPath, outputPath,
            onProgress: p =>
            {
                progressFired = true;
                Assert.True(p.TriplesLoaded > 0);
            });

        Assert.True(progressFired);
    }

    #endregion
}
