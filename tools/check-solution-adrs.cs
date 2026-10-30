#!/usr/bin/env -S dotnet
#:property TargetFramework=net10.0
//
// Checks that SkyOmega.sln lists every document in docs/adrs, each in its component's ADRs folder, and lists none
// that does not exist.
//
// The solution's ADR folders are kept by hand. They stopped being kept on 2026-05-17, and by 2026-10-30, 32 of 99
// ADRs were missing. CI runs this, so a new ADR that is not added to the solution fails the build.
//
// Rules, for each .md file under docs/adrs:
//   - the solution lists it;
//   - it sits in a solution folder named ADRs;
//   - a file in docs/adrs/<component>/ sits under a solution folder named for that component (case ignored), and a
//     file directly in docs/adrs/ under the "1 Meta" folder.
// And every docs/adrs entry the solution lists is a file that exists.
//
// Usage (from anywhere in the repository):  dotnet run tools/check-solution-adrs.cs
// Exit codes: 0 the solution and docs/adrs agree; 1 they differ (each difference printed); 2 SkyOmega.sln not found.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

string? root = Directory.GetCurrentDirectory();
while (root is not null && !File.Exists(Path.Combine(root, "SkyOmega.sln")))
    root = Path.GetDirectoryName(root);
if (root is null)
{
    Console.WriteLine("SkyOmega.sln not found in this directory or above it.");
    return 2;
}

string solution = File.ReadAllText(Path.Combine(root, "SkyOmega.sln"));

// Solution folders are projects of the solution-folder type; their files are the SolutionItems section's entries.
const string SolutionFolderType = "{2150E333-8FDC-42A3-9474-1A3956D46DE8}";
var folderNames = new Dictionary<string, string>();
var itemFolder = new Dictionary<string, string>();
foreach (Match folder in Regex.Matches(solution,
    $@"Project\(""{Regex.Escape(SolutionFolderType)}""\) = ""([^""]+)"", ""[^""]+"", ""(\{{[^}}]+\}})""(.*?)\nEndProject\b",
    RegexOptions.Singleline))
{
    string guid = folder.Groups[2].Value;
    folderNames[guid] = folder.Groups[1].Value;
    foreach (Match item in Regex.Matches(folder.Groups[3].Value, @"^\s*(\S[^=\r\n]*?) = ", RegexOptions.Multiline))
        itemFolder[item.Groups[1].Value.Trim().Replace('\\', '/')] = guid;
}

// A folder's path is its name and its parents', from the NestedProjects section.
var parentOf = new Dictionary<string, string>();
Match nested = Regex.Match(solution, @"GlobalSection\(NestedProjects\) = preSolution(.*?)EndGlobalSection", RegexOptions.Singleline);
foreach (Match pair in Regex.Matches(nested.Groups[1].Value, @"(\{[^}]+\}) = (\{[^}]+\})"))
    parentOf[pair.Groups[1].Value] = pair.Groups[2].Value;
string FolderPath(string guid)
{
    var names = new List<string>();
    for (string? g = guid; g is not null && folderNames.ContainsKey(g); g = parentOf.GetValueOrDefault(g))
        names.Insert(0, folderNames[g]);
    return string.Join("/", names);
}

var problems = new List<string>();
string adrs = Path.Combine(root, "docs", "adrs");
var onDisk = Directory.EnumerateFiles(adrs, "*.md", SearchOption.AllDirectories)
    .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
    .Order(StringComparer.Ordinal)
    .ToList();

foreach (string file in onDisk)
{
    if (!itemFolder.TryGetValue(file, out string? guid))
    {
        problems.Add($"not in the solution: {file}");
        continue;
    }
    string[] path = FolderPath(guid).Split('/');
    string[] parts = file.Split('/'); // docs, adrs, [component,] file
    string expected = parts.Length == 3 ? "1 Meta" : parts[2];
    if (path[^1] != "ADRs" || !path.Any(p => p.Equals(expected, StringComparison.OrdinalIgnoreCase)))
        problems.Add($"in the wrong solution folder: {file} is in \"{string.Join("/", path)}\"; expected an ADRs folder under \"{expected}\"");
}

foreach (string listed in itemFolder.Keys.Where(k => k.StartsWith("docs/adrs/", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
    if (!File.Exists(Path.Combine(root, listed)))
        problems.Add($"listed in the solution, but no such file: {listed}");

foreach (string problem in problems)
    Console.WriteLine(problem);
Console.WriteLine(problems.Count == 0
    ? $"OK: the solution lists all {onDisk.Count} documents in docs/adrs, each in its ADRs folder."
    : $"{problems.Count} difference(s) between SkyOmega.sln and docs/adrs.");
return problems.Count == 0 ? 0 : 1;
