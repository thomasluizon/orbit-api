using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace Orbit.Analyzers.Tests;

public sealed class UtcNowUserFacingDateAnalyzerTests
{
    [Fact]
    public Task Flags_DateOnly_From_UtcNow_In_Api_Assembly()
    {
        const string source = """
            using System;

            public sealed class DailyController
            {
                public DateOnly Today() => {|ORBIT0004:DateOnly.FromDateTime(DateTime.UtcNow)|};
            }
            """;

        return VerifyAnalyzerAsync(source, "Orbit.Api");
    }

    [Fact]
    public Task Flags_DateOnly_From_UtcNow_In_Application_Assembly()
    {
        const string source = """
            using System;

            public sealed class DailySummary
            {
                public DateOnly Today() => {|ORBIT0004:DateOnly.FromDateTime(DateTime.UtcNow)|};
            }
            """;

        return VerifyAnalyzerAsync(source, "Orbit.Application");
    }

    [Fact]
    public Task Flags_DateOnly_From_UtcNow_In_Domain_Assembly()
    {
        const string source = """
            using System;

            public sealed class Habit
            {
                public DateOnly ServerToday() => {|ORBIT0004:DateOnly.FromDateTime(DateTime.UtcNow)|};
            }
            """;

        return VerifyAnalyzerAsync(source, "Orbit.Domain");
    }

    [Fact]
    public Task Flags_DateOnly_From_UtcNow_Arithmetic_Chain_In_Infrastructure_Assembly()
    {
        const string source = """
            using System;

            public sealed class ReminderWindow
            {
                public DateOnly Min() => {|ORBIT0004:DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1))|};
            }
            """;

        return VerifyAnalyzerAsync(source, "Orbit.Infrastructure");
    }

    [Fact]
    public Task Allows_DateOnly_From_Timezone_Converted_Instant()
    {
        const string source = """
            using System;

            public sealed class UserDateService
            {
                public DateOnly UserToday(TimeZoneInfo zone) =>
                    DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone));
            }
            """;

        return VerifyAnalyzerAsync(source, "Orbit.Infrastructure");
    }

    [Fact]
    public Task Allows_Plain_UtcNow_Instant()
    {
        const string source = """
            using System;

            public sealed class AuditEntry
            {
                public DateTime Snapshot() => DateTime.UtcNow;
            }
            """;

        return VerifyAnalyzerAsync(source, "Orbit.Application");
    }

    [Fact]
    public Task Ignores_Assemblies_Outside_The_Four_Orbit_Projects()
    {
        const string source = """
            using System;

            public sealed class Fixture
            {
                public DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow);
            }
            """;

        return VerifyAnalyzerAsync(source, "Orbit.Benchmarks");
    }

    private static Task VerifyAnalyzerAsync(string source, string assemblyName)
    {
        var test = new CSharpAnalyzerTest<UtcNowUserFacingDateAnalyzer, DefaultVerifier>
        {
            TestCode = source,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
            MarkupOptions = MarkupOptions.UseFirstDescriptor,
        };

        test.SolutionTransforms.Add((solution, projectId) =>
            solution.WithProjectAssemblyName(projectId, assemblyName));

        return test.RunAsync();
    }
}
