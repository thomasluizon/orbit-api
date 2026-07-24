using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace Orbit.Analyzers.Tests;

public sealed class UtcNowUserFacingDateAnalyzerTests
{
    [Fact]
    public Task Flags_Bare_UtcNow_Read_In_Application_Assembly()
    {
        const string source = """
            using System;

            public sealed class SlipDetector
            {
                public DateTime Snapshot() => {|ORBIT0004:DateTime.UtcNow|};
            }
            """;

        return VerifyAnalyzerAsync(source, "Orbit.Application");
    }

    [Fact]
    public Task Exempts_AtUtc_Timestamp_Assignment()
    {
        const string source = """
            using System;

            public sealed class AuditEntry
            {
                public DateTime CreatedAtUtc { get; private set; }

                public void Touch() => CreatedAtUtc = DateTime.UtcNow;
            }
            """;

        return VerifyAnalyzerAsync(source, "Orbit.Application");
    }

    [Fact]
    public Task Exempts_Cache_Key_Construction()
    {
        const string source = """
            using System;

            public sealed class SummaryKeys
            {
                public string BuildKey(int userId)
                {
                    var cacheKey = $"summary:{userId}:{DateTime.UtcNow.Hour}";
                    return cacheKey;
                }
            }
            """;

        return VerifyAnalyzerAsync(source, "Orbit.Application");
    }

    [Fact]
    public Task Exempts_TimeZoneInfo_Conversion_Into_User_Timezone()
    {
        const string source = """
            using System;

            public sealed class UserClock
            {
                public DateTime UserNow(TimeZoneInfo zone) =>
                    TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
            }
            """;

        return VerifyAnalyzerAsync(source, "Orbit.Application");
    }

    [Fact]
    public Task Flags_DateOnly_From_UtcNow_Even_On_A_Timestamp_Line()
    {
        const string source = """
            using System;

            public sealed class DailySummary
            {
                public DateOnly TodayAtUtc() => {|ORBIT0004:DateOnly.FromDateTime(DateTime.UtcNow)|};
            }
            """;

        return VerifyAnalyzerAsync(source, "Orbit.Application");
    }

    [Fact]
    public Task Flags_DateOnly_From_UtcNow_Arithmetic_Chain()
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
    public Task Domain_Assembly_Allows_Plain_Instants_But_Flags_DateOnly_Derivation()
    {
        const string source = """
            using System;

            public sealed class Habit
            {
                public DateTime DeactivatedAt { get; private set; }

                public void Deactivate() => DeactivatedAt = DateTime.UtcNow;

                public DateOnly ServerToday() => {|ORBIT0004:DateOnly.FromDateTime(DateTime.UtcNow)|};
            }
            """;

        return VerifyAnalyzerAsync(source, "Orbit.Domain");
    }

    [Fact]
    public Task Ignores_Assemblies_Outside_The_Four_Orbit_Projects()
    {
        const string source = """
            using System;

            public sealed class Fixture
            {
                public DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow);

                public DateTime Now() => DateTime.UtcNow;
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
