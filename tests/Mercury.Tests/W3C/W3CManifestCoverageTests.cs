// Licensed under the MIT License.

using Xunit;

namespace SkyOmega.Mercury.Tests.W3C;

/// <summary>
/// The guard of ADR-059 D4: every manifest in the two W3C submodules is loaded by a suite or listed, with its reason,
/// in <see cref="W3CTestContext.ManifestsNotLoaded"/>, so a submodule update cannot add tests, or take away an include,
/// without a test failing. Built 2026-11-10, when rdf-tests 9fafbec (#396) removed the RDF 1.2 manifests' includes
/// of the RDF 1.1 ones and about 980 tests stopped loading with every test still green.
/// </summary>
public class W3CManifestCoverageTests
{
    private readonly ITestOutputHelper _output;

    public W3CManifestCoverageTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task EveryManifest_IsLoadedByASuite_OrListedAsNotLoaded()
    {
        Assert.SkipUnless(W3CTestContext.IsAvailable, "W3C RDF test suite not available");
        Assert.SkipUnless(JsonLdTestContext.IsAvailable, "W3C JSON-LD test suite not available");
        var cancellationToken = TestContext.Current.CancellationToken;

        // The manifests the suites read, by their path under tests/: each format's suites with everything they
        // include, read by one parser per format as W3CTestContext.TestsOf reads them, and JSON-LD's toRdf manifest.
        var loaded = new HashSet<string>(StringComparer.Ordinal);
        var notRead = new List<string>();
        foreach (var format in W3CTestContext.Formats)
        {
            var parser = new W3CManifestParser();
            foreach (var suite in W3CTestContext.SuitesOf(format))
                await parser.ParseAsync(W3CTestContext.GetManifestPath(suite), cancellationToken);

            foreach (var path in parser.ManifestsRead)
                loaded.Add("w3c-rdf-tests/" + Path.GetRelativePath(W3CTestContext.TestsRoot, path).Replace('\\', '/'));
            foreach (var (path, why) in parser.ManifestsNotRead)
                notRead.Add($"{format}: {Path.GetRelativePath(W3CTestContext.TestsRoot, path).Replace('\\', '/')}: {why}");
        }
        loaded.Add("w3c-json-ld-api/tests/" + JsonLdTestContext.ToRdfManifest);

        // Every manifest in the two submodules: rdf-tests names each Turtle manifest *manifest*.ttl (manifest.ttl,
        // manifest-sparql11-query.ttl, sameTerm-manifest.ttl, …), json-ld-api each JSON-LD one tests/*manifest.jsonld.
        var all = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(W3CTestContext.TestsRoot, "*manifest*.ttl", SearchOption.AllDirectories))
            all.Add("w3c-rdf-tests/" + Path.GetRelativePath(W3CTestContext.TestsRoot, path).Replace('\\', '/'));
        foreach (var path in Directory.EnumerateFiles(Path.Combine(JsonLdTestContext.TestsRoot, "tests"), "*manifest.jsonld"))
            all.Add("w3c-json-ld-api/" + Path.GetRelativePath(JsonLdTestContext.TestsRoot, path).Replace('\\', '/'));

        var neither = all.Where(m => !loaded.Contains(m) && !W3CTestContext.ManifestsNotLoaded.ContainsKey(m)).ToList();
        var listedButLoaded = W3CTestContext.ManifestsNotLoaded.Keys.Where(loaded.Contains).ToList();
        var listedButMissing = W3CTestContext.ManifestsNotLoaded.Keys.Where(m => !all.Contains(m)).ToList();

        _output.WriteLine($"{all.Count} manifests: {all.Count(loaded.Contains)} loaded, {W3CTestContext.ManifestsNotLoaded.Count} listed as not loaded");
        foreach (var m in neither)
            _output.WriteLine($"neither loaded nor listed: {m}");
        foreach (var m in listedButLoaded)
            _output.WriteLine($"listed as not loaded, but a suite loads it: {m}");
        foreach (var m in listedButMissing)
            _output.WriteLine($"listed as not loaded, but not in the submodules: {m}");
        foreach (var m in notRead)
            _output.WriteLine($"a suite could not read: {m}");

        Assert.True(neither.Count == 0 && listedButLoaded.Count == 0 && listedButMissing.Count == 0 && notRead.Count == 0,
            $"{neither.Count} manifests neither loaded nor listed, {listedButLoaded.Count} listed but loaded, " +
            $"{listedButMissing.Count} listed but missing, {notRead.Count} not readable; the test output names each.");
    }

    [Fact]
    public async Task EveryEntryOfALoadedManifest_IsATestOfAKindASuiteRuns()
    {
        Assert.SkipUnless(W3CTestContext.IsAvailable, "W3C RDF test suite not available");
        var cancellationToken = TestContext.Current.CancellationToken;

        // Two ways an entry of a loaded manifest is absent from the run, neither passed, failed nor skipped (ADR-059
        // Context): the parser cannot make a test of it (no description, no mf:action), or the test's rdf:type is one
        // the parser does not map, so it is read as Unknown, which no test method asks for.
        var notRead = new List<string>();
        var unknown = new List<string>();
        foreach (var format in W3CTestContext.Formats)
        {
            var parser = new W3CManifestParser();
            foreach (var suite in W3CTestContext.SuitesOf(format))
            {
                foreach (var test in await parser.ParseAsync(W3CTestContext.GetManifestPath(suite), cancellationToken))
                {
                    if (test.Type == W3CTestType.Unknown)
                        unknown.Add($"{format}: {test.Id}");
                }
            }
            foreach (var entry in parser.EntriesNotRead)
                notRead.Add($"{format}: {entry}");
        }

        foreach (var entry in notRead)
            _output.WriteLine($"not made a test of: {entry}");
        foreach (var test in unknown)
            _output.WriteLine($"of an unknown kind: {test}");

        Assert.True(notRead.Count == 0 && unknown.Count == 0,
            $"{notRead.Count} entries are not made tests of, {unknown.Count} tests are of a kind no suite runs; the test output names each.");
    }
}
