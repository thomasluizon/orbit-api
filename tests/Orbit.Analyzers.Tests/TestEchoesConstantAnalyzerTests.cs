using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace Orbit.Analyzers.Tests;

public sealed class TestEchoesConstantAnalyzerTests
{
    private const string ProductionAssembly = "Orbit.Domain";
    private const string TestAssembly = "Orbit.Domain.Tests";

    private const string ProductionSource = """
        public static class Limits
        {
            public const int Max = 100;
            public const int Min = -1;
            public const bool Enabled = true;
            public const string? Missing = null;
            public static readonly decimal Price = 9.9m;
            public static readonly int[] Steps = new[] { 1 };
            public static int Mutable = 100;
            public static int Computed => 100;
        }

        public static class ApiVersions
        {
            public static readonly string Current = "v2";
        }
        """;

    private const string AssertionStubs = """
        namespace Xunit
        {
            public static class Assert
            {
                public static void Equal<T>(T expected, T actual) { }
                public static void NotEqual<T>(T expected, T actual) { }
            }
        }

        public static class AssertionExtensions
        {
            public static Assertion<T> Should<T>(this T subject) => new Assertion<T>();
        }

        public sealed class Assertion<T>
        {
            public void Be(T expected, string because = "") { }
            public void NotBe(T expected) { }
        }
        """;

    [Fact]
    public Task Flags_A_Production_Const_Asserted_Equal_To_A_Literal()
    {
        const string source = """
            public sealed class LimitsTests
            {
                public void Max() => {|ORBIT0006:Limits.Max.Should().Be(100)|};

                public void Reason() => {|ORBIT0006:Limits.Max.Should().Be(100, "the ticket says so")|};

                public void Negative() => {|ORBIT0006:Limits.Min.Should().Be(-1)|};

                public void Flag() => {|ORBIT0006:Limits.Enabled.Should().Be(true)|};

                public void Empty() => {|ORBIT0006:(Limits.Missing).Should().Be(null)|};
            }
            """;

        return VerifyAnalyzerAsync(source, TestAssembly);
    }

    [Fact]
    public Task Flags_A_Production_Static_Readonly_Value_In_Each_Argument_Order_Of_Assert_Equal()
    {
        const string source = """
            using Xunit;

            public sealed class VersionTests
            {
                public void LiteralFirst() => {|ORBIT0006:Assert.Equal("v2", ApiVersions.Current)|};

                public void SubjectFirst() => {|ORBIT0006:Assert.Equal(ApiVersions.Current, "v2")|};

                public void Number() => {|ORBIT0006:Assert.Equal(9.9m, Limits.Price)|};

                public void Fluent() => {|ORBIT0006:ApiVersions.Current.Should().Be("v2")|};
            }
            """;

        return VerifyAnalyzerAsync(source, TestAssembly);
    }

    [Fact]
    public Task Allows_A_Value_That_The_Code_Computes()
    {
        const string source = """
            using Xunit;

            public sealed class Result
            {
                public int Value { get; } = 100;
            }

            public sealed class ResultTests
            {
                public void Value(Result result) => result.Value.Should().Be(100);

                public void Property() => Limits.Computed.Should().Be(100);

                public void Mutable() => Assert.Equal(100, Limits.Mutable);

                public void Element() => Limits.Steps[0].Should().Be(1);
            }
            """;

        return VerifyAnalyzerAsync(source, TestAssembly);
    }

    [Fact]
    public Task Allows_A_Comparison_That_Is_Not_Equality_With_A_Literal()
    {
        const string source = """
            using Xunit;

            public sealed class LimitsTests
            {
                public void Against(int actual) => Assert.Equal(Limits.Max, actual);

                public void Fluent(int actual) => actual.Should().Be(Limits.Max);

                public void Different() => Limits.Max.Should().NotBe(0);

                public void DifferentAssert() => Assert.NotEqual(0, Limits.Max);
            }
            """;

        return VerifyAnalyzerAsync(source, TestAssembly);
    }

    [Fact]
    public Task Allows_A_Const_That_The_Test_Project_Declares()
    {
        const string source = """
            public static class Expected
            {
                public const int Max = 100;
            }

            public sealed class ExpectedTests
            {
                public void Max() => Expected.Max.Should().Be(100);
            }
            """;

        return VerifyAnalyzerAsync(source, TestAssembly);
    }

    [Theory]
    [InlineData("Orbit.Testing.Tests")]
    [InlineData("ThirdParty.Library")]
    public Task Allows_A_Const_From_An_Assembly_That_Is_Not_Orbit_Production(string declaringAssembly)
    {
        const string source = """
            public sealed class LimitsTests
            {
                public void Max() => Limits.Max.Should().Be(100);
            }
            """;

        return VerifyAnalyzerAsync(source, TestAssembly, declaringAssembly);
    }

    [Fact]
    public Task Allows_The_Same_Assertion_Outside_A_Test_Project()
    {
        const string source = """
            public sealed class StartupCheck
            {
                public void Max() => Limits.Max.Should().Be(100);
            }
            """;

        return VerifyAnalyzerAsync(source, "Orbit.Api");
    }

    private static Task VerifyAnalyzerAsync(
        string source, string assemblyName, string declaringAssembly = ProductionAssembly)
    {
        var test = new CSharpAnalyzerTest<TestEchoesConstantAnalyzer, DefaultVerifier>
        {
            TestCode = source,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
            MarkupOptions = MarkupOptions.UseFirstDescriptor,
        };
        test.TestState.Sources.Add(AssertionStubs);
        test.TestState.AdditionalProjects[declaringAssembly].Sources.Add(ProductionSource);
        test.TestState.AdditionalProjectReferences.Add(declaringAssembly);

        test.SolutionTransforms.Add((solution, projectId) =>
            solution.GetProject(projectId)!.Name == declaringAssembly
                ? solution
                : solution.WithProjectAssemblyName(projectId, assemblyName));

        return test.RunAsync();
    }
}
