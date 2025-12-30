// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace SkyOmega.Mercury.Diagnostics;

/// <summary>
/// Severity level for diagnostics, ordered from most to least severe.
/// </summary>
public enum DiagnosticSeverity : byte
{
    /// <summary>
    /// A fatal error that prevents further processing.
    /// </summary>
    Error = 0,

    /// <summary>
    /// A potential problem that doesn't prevent execution but may indicate issues.
    /// </summary>
    Warning = 1,

    /// <summary>
    /// Informational message about query execution or optimization.
    /// </summary>
    Info = 2,

    /// <summary>
    /// A suggestion for improvement (e.g., adding an index, rewriting a pattern).
    /// </summary>
    Hint = 3
}
