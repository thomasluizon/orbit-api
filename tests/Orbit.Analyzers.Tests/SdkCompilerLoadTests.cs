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
    private const string MvcStub = """
        namespace Microsoft.AspNetCore.Mvc
        {
            public abstract class ControllerBase { }
        }

        namespace Microsoft.AspNetCore.Authorization
        {
            public class AuthorizeAttribute : System.Attribute { }

            public sealed class AllowAnonymousAttribute : System.Attribute { }
        }

        namespace App
        {
            public sealed class UsersController : Microsoft.AspNetCore.Mvc.ControllerBase
            {
                public string Get() => "users";
            }
        }
        """;

    private const string RollbackStub = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;

        namespace Microsoft.EntityFrameworkCore.Storage
        {
            public interface IDbContextTransaction : IAsyncDisposable
            {
                Task CommitAsync(CancellationToken cancellationToken);
                Task RollbackAsync(CancellationToken cancellationToken);
            }
        }

        namespace App
        {
            using Microsoft.EntityFrameworkCore.Storage;

            public sealed class FakeTransaction : IDbContextTransaction
            {
                public Task CommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
                public Task RollbackAsync(CancellationToken cancellationToken) => Task.CompletedTask;
                public ValueTask DisposeAsync() => default;
            }

            public sealed class Service
            {
                public async Task RunAsync(CancellationToken ct)
                {
                    await using var tx = await Task.FromResult<IDbContextTransaction>(new FakeTransaction());
                    try
                    {
                        await tx.CommitAsync(ct);
                    }
                    catch
                    {
                        await tx.RollbackAsync(ct);
                        throw;
                    }
                }
            }
        }
        """;

    private const string UtcNowStub = """
        using System;

        public sealed class SlipDetector
        {
            public DateTime Snapshot() => DateTime.UtcNow;
        }
        """;

    private const string DbSetStub = """
        namespace Microsoft.EntityFrameworkCore
        {
            public class DbContext
            {
                public DbSet<TEntity> Set<TEntity>() where TEntity : class => new DbSet<TEntity>();

                protected internal virtual void OnModelCreating(ModelBuilder modelBuilder) { }
            }

            public class DbSet<TEntity> where TEntity : class { }

            public class ModelBuilder { }
        }

        namespace App
        {
            using Microsoft.EntityFrameworkCore;

            public sealed class Habit { }

            public sealed class AppDbContext : DbContext
            {
                public DbSet<Habit> Habits => Set<Habit>();

                protected internal override void OnModelCreating(ModelBuilder modelBuilder) { }
            }
        }
        """;

    private const string CommentStub = """
        public sealed class Narrated
        {
            // narration comment
            public int Value => 1;
        }
        """;

    public static TheoryData<string, string> Violations => new()
    {
        { "ORBIT0001", CommentStub },
        { "ORBIT0002", RollbackStub },
        { "ORBIT0003", MvcStub },
        { "ORBIT0004", UtcNowStub },
        { "ORBIT0005", DbSetStub },
    };

    [Theory]
    [MemberData(nameof(Violations))]
    public async Task SdkCompiler_LoadsAnalyzer_AndReportsRule(string ruleId, string source)
    {
        var output = await CompileWithSdkCompilerAsync(source);

        Assert.DoesNotContain("CS9057", output, StringComparison.Ordinal);
        Assert.Contains($"error {ruleId}:", output, StringComparison.Ordinal);
    }

    private static async Task<string> CompileWithSdkCompilerAsync(string source)
    {
        var compilerPath = Path.Combine(ReadBuildMetadata("RoslynTargetsPath"), "bincore", "csc.dll");
        Assert.True(File.Exists(compilerPath), $"The SDK compiler is missing at {compilerPath}.");

        var workDirectory = Directory.CreateTempSubdirectory("orbit-analyzer-load-");
        try
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
                $"/out:{Path.Combine(workDirectory.FullName, "Orbit.Application.dll")}",
                $"/analyzer:{typeof(NoCommentsAnalyzer).Assembly.Location}",
            };
            arguments.AddRange(references.Select(path => $"/reference:{path}"));
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
        finally
        {
            workDirectory.Delete(recursive: true);
        }
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
