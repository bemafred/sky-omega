// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Reflection.Emit;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Runtime.IO;
using SkyOmega.Mercury.Serving;
using Xunit;

namespace SkyOmega.Mercury.Tests.Serving;

/// <summary>The attach target's pipe and the client's answer when nothing is there; the version line.</summary>
public sealed class AttachAndVersionTests
{
    [Theory]
    [InlineData("mcp", "mercury-mcp")]
    [InlineData("MCP", "mercury-mcp")]
    [InlineData("cli", "cli")] // dropped (ADR-065 D7): the name of a pipe, as any other
    [InlineData("my-pipe", "my-pipe")]
    public void AttachPipeName(string target, string pipe)
    {
        Assert.Equal(pipe, MercuryPorts.AttachPipeName(target));
    }

    [Fact]
    public async Task Attach_NothingThere_SaysSo_AndExitsWith1()
    {
        var target = $"mt-{Guid.NewGuid():N}"[..16];
        using var output = new StringWriter();

        int exitCode = await AttachClient.RunToExitCodeAsync(AttachTarget.Named(target), output);

        Assert.Equal(1, exitCode);
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal($"Attaching to {target} via pipe '{target}'...", lines[0]);
        Assert.StartsWith("Error: ", lines[^1]);
    }

    [Fact]
    public void ToolVersion_IsTheInformationalVersion()
    {
        var assembly = typeof(ToolVersion).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        Assert.Equal(version, ToolVersion.Of(assembly));
        Assert.Equal($"mercury {version}", ToolVersion.Line("mercury", assembly));
    }

    [Fact]
    public void ToolVersion_None_IsUnknown()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("no-version"), AssemblyBuilderAccess.Run);

        Assert.Equal("unknown", ToolVersion.Of(assembly));
    }
}
