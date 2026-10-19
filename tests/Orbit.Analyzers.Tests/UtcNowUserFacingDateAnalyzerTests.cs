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

    [Fact]
    public Task Exempts_Instant_Passed_To_AtUtc_Parameter()
    {
        const string source = """
            using System;
            public sealed class Sessions
            {
                private static void Revoke(DateTime revokedAtUtc) { }
                public void Run() => Revoke(DateTime.UtcNow);
            }
            """;
        return VerifyAnalyzerAsync(source, "Orbit.Infrastructure");
    }

    [Fact]
    public Task Exempts_Local_Used_Only_By_AtUtc_Sinks()
    {
        const string source = """
            using System;
            public sealed class Sessions
            {
                public DateTime UpdatedAtUtc { get; private set; }
                private static void Revoke(DateTime revokedAtUtc) { }
                public void Run()
                {
                    var nowUtc = DateTime.UtcNow;
                    UpdatedAtUtc = nowUtc;
                    Revoke(nowUtc);
                }
            }
            """;
        return VerifyAnalyzerAsync(source, "Orbit.Infrastructure");
    }

    [Fact]
    public Task Flags_Local_With_Additional_Calendar_Use()
    {
        const string source = """
            using System;
            public sealed class Sessions
            {
                private static void Revoke(DateTime revokedAtUtc) { }
                public DateOnly Run()
                {
                    var nowUtc = {|ORBIT0004:DateTime.UtcNow|};
                    Revoke(nowUtc);
                    return DateOnly.FromDateTime(nowUtc);
                }
            }
            """;
        return VerifyAnalyzerAsync(source, "Orbit.Infrastructure");
    }

    [Fact]
    public Task Flags_Local_With_Timezone_Conversion_And_AtUtc_Sink()
    {
        const string source = """
            using System;
            public sealed class Sessions
            {
                private static void Revoke(DateTime revokedAtUtc) { }
                public DateTime Run(TimeZoneInfo zone)
                {
                    var nowUtc = {|ORBIT0004:DateTime.UtcNow|};
                    Revoke(nowUtc);
                    return TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);
                }
            }
            """;
        return VerifyAnalyzerAsync(source, "Orbit.Infrastructure");
    }

    [Fact]
    public Task Flags_Assignment_To_Non_Property_AtUtc_Local()
    {
        const string source = """
            using System;
            public sealed class Sessions
            {
                public DateTime Run()
                {
                    DateTime createdAtUtc;
                    createdAtUtc =
                        {|ORBIT0004:DateTime.UtcNow|};
                    return createdAtUtc;
                }
            }
            """;
        return VerifyAnalyzerAsync(source, "Orbit.Infrastructure");
    }

    [Fact]
    public Task Flags_Unused_Or_Reassigned_Utc_Local()
    {
        const string source = """
            using System;
            public sealed class Sessions
            {
                private static void Revoke(DateTime revokedAtUtc) { }
                public void Run()
                {
                    var unusedUtc = {|ORBIT0004:DateTime.UtcNow|};
                    var nowUtc = {|ORBIT0004:DateTime.UtcNow|};
                    nowUtc = DateTime.MinValue;
                    Revoke(nowUtc);
                }
            }
            """;
        return VerifyAnalyzerAsync(source, "Orbit.Infrastructure");
    }

    [Fact]
    public Task Exempts_Expiry_Arithmetic_In_Constructor_Named_Argument()
    {
        const string source = """
            using System;
            public sealed class Challenge
            {
                public Challenge(int attempts, DateTime expiresAtUtc) { }
                public static Challenge Create() => new(expiresAtUtc:
                    DateTime.UtcNow.AddMinutes(5), attempts: 0);
            }
            """;
        return VerifyAnalyzerAsync(source, "Orbit.Infrastructure");
    }

    [Fact]
    public Task Flags_Date_Truncation_Even_When_Consumed_By_AtUtc_Parameter()
    {
        const string source = """
            using System;
            public sealed class Sessions
            {
                private static void Revoke(DateTime revokedAtUtc) { }
                public void Run() => Revoke({|ORBIT0004:DateTime.UtcNow|}.Date);
            }
            """;
        return VerifyAnalyzerAsync(source, "Orbit.Infrastructure");
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
