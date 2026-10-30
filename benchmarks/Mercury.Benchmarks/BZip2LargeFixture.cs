using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SkyOmega.Mercury.Benchmarks;

/// <summary>
/// A 50 MB N-Triples document shaped like Wikidata's (entity subjects, a label, an instance-of and an integer per
/// entity) and its <c>bzip2 -9</c> compression: about 55 blocks, enough to keep 14 decompression workers busy, where
/// the committed 410 KB fixture has about four.
/// </summary>
/// <remarks>
/// <para>It replaces <c>/tmp/multiblock-large.txt.bz2</c>, which the measurement classes once read from the test suite
/// and which existed only on the machine that made it.</para>
/// <para>Mercury has a bzip2 decompressor and no compressor, so the compressed file is made by the bzip2 tool, which
/// must be on PATH, and kept in the temp directory for later runs. The text is generated the same way every time; the
/// file name carries a version, to change when the generator does.</para>
/// </remarks>
internal static class BZip2LargeFixture
{
    private const int TextBytes = 50 * 1024 * 1024;
    private const string FileName = "wikidata-shaped-50mb-v1.nt.bz2";
    private static readonly TimeSpan CompressionTimeLimit = TimeSpan.FromMinutes(10);

    /// <summary>The N-Triples text, about 50 MB.</summary>
    public static byte[] Text()
    {
        var random = new Random(2026);
        var text = new StringBuilder(TextBytes + 512);
        for (int entity = 1; text.Length < TextBytes; entity++)
        {
            var id = entity.ToString(CultureInfo.InvariantCulture);
            text.Append("<http://www.wikidata.org/entity/Q").Append(id)
                .Append("> <http://www.w3.org/2000/01/rdf-schema#label> \"entity ").Append(id).Append("\"@en .\n");
            text.Append("<http://www.wikidata.org/entity/Q").Append(id)
                .Append("> <http://www.wikidata.org/prop/direct/P31> <http://www.wikidata.org/entity/Q")
                .Append(random.Next(1, 100_000).ToString(CultureInfo.InvariantCulture)).Append("> .\n");
            text.Append("<http://www.wikidata.org/entity/Q").Append(id)
                .Append("> <http://www.wikidata.org/prop/direct/P1082> \"")
                .Append(random.Next().ToString(CultureInfo.InvariantCulture))
                .Append("\"^^<http://www.w3.org/2001/XMLSchema#integer> .\n");
        }
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    /// <summary>The text compressed by <c>bzip2 -9</c>: made on the first run, then read from the temp directory.</summary>
    public static byte[] Compressed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mercury-benchmarks");
        var compressedPath = Path.Combine(directory, FileName);
        if (File.Exists(compressedPath))
            return File.ReadAllBytes(compressedPath);

        Directory.CreateDirectory(directory);
        // A name of its own, so two benchmark processes making the fixture at once do not write one file.
        var textPath = Path.Combine(directory, $"{Guid.NewGuid():N}.nt");
        File.WriteAllBytes(textPath, Text());
        try
        {
            using var bzip2 = Process.Start(new ProcessStartInfo("bzip2", ["-9", "-f", textPath]) { UseShellExecute = false })
                ?? throw new InvalidOperationException("bzip2 did not start.");
            if (!bzip2.WaitForExit(CompressionTimeLimit))
            {
                bzip2.Kill();
                throw new TimeoutException($"bzip2 did not finish within {CompressionTimeLimit}.");
            }
            if (bzip2.ExitCode != 0)
                throw new InvalidOperationException($"bzip2 exited with {bzip2.ExitCode}.");
        }
        catch (Win32Exception e)
        {
            throw new InvalidOperationException(
                "The large bzip2 fixture is made by the bzip2 tool, which is not on PATH. Install bzip2, or put a " +
                $"bzip2 -9 file of 50 MB of text at {compressedPath}.", e);
        }
        finally
        {
            File.Delete(textPath);
        }

        File.Move(textPath + ".bz2", compressedPath, overwrite: true);
        return File.ReadAllBytes(compressedPath);
    }
}
