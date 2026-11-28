using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Orbit.Analyzers.Tests;

/// <summary>
/// Compiles one violation for each rule with the <c>csc</c> of the SDK that builds this repository, with
/// <c>Orbit.Analyzers.dll</c> as an analyzer. The in-process tests cannot see a compiler that refuses to load the
/// analyzer (<c>CS9057</c>), because they run against the Roslyn package that the analyzer references.
/// </summary>
public sealed class SdkCompilerLoadTests
{
    private const string UtcNowDateOnlyStub = """
        using System;

        public sealed class SlipDetector
        {
            public DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow);
        }
        """;

    private const string CommentStub = """
        public sealed class Narrated
        {
            // narration comment
            public int Value => 1;
        }
        """;

    private const string SourceReadStub = """
        public static class SourceReader
        {
            public static string Read() => System.IO.File.ReadAllText("Handler.cs");
        }
        """;

    private const string ProductionConstantStub = """
        public static class Limits
        {
            public const int Max = 100;
        }
        """;

    private const string ConstantEchoStub = """
        public sealed class Assertion
        {
            public void Be(int expected) { }
        }

        public static class LimitsTests
        {
            public static void Max() => Limits.Max.Should().Be(100);

            private static Assertion Should(this int value) => new Assertion();
        }
        """;

    public static TheoryData<string, string, string, string?> Violations => new()
    {
        { "ORBIT0001", "Orbit.Application", CommentStub, null },
        { "ORBIT0004", "Orbit.Application", UtcNowDateOnlyStub, null },
        { "ORBIT0005", "Orbit.Application.Tests", SourceReadStub, null },
        { "ORBIT0006", "Orbit.Application.Tests", ConstantEchoStub, ProductionConstantStub },
    };

    [Theory]
    [MemberData(nameof(Violations))]
    public async Task SdkCompiler_LoadsAnalyzer_AndReportsRule(
        string ruleId, string assemblyName, string source, string? productionSource)
    {
        var output = await CompileWithSdkCompilerAsync(assemblyName, source, productionSource);

        Assert.DoesNotContain("CS9057", output, StringComparison.Ordinal);
        Assert.Contains($"error {ruleId}:", output, StringComparison.Ordinal);
    }

    private static async Task<string> CompileWithSdkCompilerAsync(
        string assemblyName, string source, string? productionSource)
    {
        var compilerPath = Path.Combine(ReadBuildMetadata("RoslynTargetsPath"), "bincore", "csc.dll");
        Assert.True(File.Exists(compilerPath), $"The SDK compiler is missing at {compilerPath}.");

        var workDirectory = Directory.CreateTempSubdirectory("orbit-analyzer-load-");
        try
        {
            string[] references = [];
            if (productionSource is not null)
            {
                await RunCompilerAsync(compilerPath, workDirectory, "Orbit.Domain", productionSource, references);
                references = [Path.Combine(workDirectory.FullName, "Orbit.Domain.dll")];
            }

            return await RunCompilerAsync(compilerPath, workDirectory, assemblyName, source, references);
        }
        finally
        {
            workDirectory.Delete(recursive: true);
        }
    }

    private static async Task<string> RunCompilerAsync(
        string compilerPath,
        DirectoryInfo workDirectory,
        string assemblyName,
        string source,
        IReadOnlyCollection<string> projectReferences)
    {
        var sourcePath = Path.Combine(workDirectory.FullName, "Violation.cs");
        await File.WriteAllTextAsync(sourcePath, source);

        var runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
        var references = Directory.GetFiles(runtimeDirectory, "*.dll")
            .Where(path => Path.GetFileName(path).StartsWith("System.", StringComparison.Ordinal)
                || Path.GetFileName(path) is "netstandard.dll" or "mscorlib.dll");
        var arguments = new List<string>
        {
            "/nologo",
            "/nostdlib+",
            "/target:library",
            "/langversion:latest",
            $"/out:{Path.Combine(workDirectory.FullName, $"{assemblyName}.dll")}",
            $"/analyzer:{typeof(NoCommentsAnalyzer).Assembly.Location}",
        };
        arguments.AddRange(references.Concat(projectReferences).Select(path => $"/reference:{path}"));
        arguments.Add(sourcePath);

        var responseFile = Path.Combine(workDirectory.FullName, "csc.rsp");
        await File.WriteAllLinesAsync(responseFile, arguments.Select(argument => $"\"{argument}\""));

        var startInfo = new ProcessStartInfo(ReadBuildMetadata("DotnetHostPath"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(compilerPath);
        startInfo.ArgumentList.Add("/noconfig");
        startInfo.ArgumentList.Add($"@{responseFile}");

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return await standardOutput + await standardError;
    }

    private static string ReadBuildMetadata(string key)
    {
        var value = typeof(SdkCompilerLoadTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == key)?.Value;
        if (string.IsNullOrEmpty(value))
            throw new InvalidOperationException($"The build did not record the {key} metadata.");
        return value;
    }
}
