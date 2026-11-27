using System;

namespace Orbit.Analyzers;

internal static class TestAssemblyName
{
    public static bool Matches(string? assemblyName) =>
        assemblyName is not null && assemblyName.EndsWith(".Tests", StringComparison.Ordinal);
}
