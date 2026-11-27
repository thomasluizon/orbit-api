using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace Orbit.Analyzers.Tests;

public sealed class TestReadsSourceTextAnalyzerTests
{
    private const string TestAssembly = "Orbit.Application.Tests";
    private const string TestFile = "/0/Application/HandlerTests.cs";

    private const string InlineSourceRead = """
        using System.IO;

        public sealed class HandlerTests
        {
            public string Source(string dir) => File.ReadAllText(Path.Combine(dir, "src", "Handler.cs"));
        }
        """;

    [Fact]
    public Task Flags_A_Source_File_Literal_Inside_The_Path_Argument()
    {
        const string source = """
            using System.IO;

            public sealed class HandlerTests
            {
                public string Source(string dir) => {|ORBIT0005:File.ReadAllText(Path.Combine(dir, "src", "Handler.cs"))|};
            }
            """;

        return VerifyAnalyzerAsync(source, TestAssembly, TestFile);
    }

    [Fact]
    public Task Flags_A_Source_File_Path_Held_In_A_Local_Variable()
    {
        const string source = """
            using System.IO;

            public sealed class JobTests
            {
                public string[] Lines(string root)
                {
                    var path = Path.Combine(root, "Job.cs");
                    return {|ORBIT0005:File.ReadAllLines(path)|};
                }
            }
            """;

        return VerifyAnalyzerAsync(source, TestAssembly, TestFile);
    }

    [Fact]
    public Task Flags_Each_Other_Text_Read_Of_The_File_Type()
    {
        const string source = """
            using System.Collections.Generic;
            using System.IO;
            using System.Threading.Tasks;
            using static System.IO.File;

            public sealed class JobTests
            {
                public Task<string> Text() => {|ORBIT0005:File.ReadAllTextAsync("Job.cs")|};

                public Task<string[]> Lines() => {|ORBIT0005:System.IO.File.ReadAllLinesAsync("Job.cs")|};

                public IEnumerable<string> Stream() => {|ORBIT0005:File.ReadLines("Job.cs")|};

                public string Imported() => {|ORBIT0005:ReadAllText("Job.cs")|};
            }
            """;

        return VerifyAnalyzerAsync(source, TestAssembly, TestFile);
    }

    [Fact]
    public Task Allows_A_Json_Fixture_Read()
    {
        const string source = """
            using System.IO;

            public sealed class FixtureTests
            {
                public string Fixture(string dir)
                {
                    var path = Path.Combine(dir, "Fixtures", "capture.json");
                    return File.ReadAllText(path) + File.ReadAllText(Path.Combine(dir, "capture.json"));
                }
            }
            """;

        return VerifyAnalyzerAsync(source, TestAssembly, TestFile);
    }

    [Fact]
    public Task Allows_A_Source_File_Write()
    {
        const string source = """
            using System.IO;

            public sealed class CompilerTests
            {
                public void Arrange(string dir) => File.WriteAllText(Path.Combine(dir, "Stub.cs"), "public class Stub;");
            }
            """;

        return VerifyAnalyzerAsync(source, TestAssembly, TestFile);
    }

    [Fact]
    public Task Allows_A_Read_Method_Of_Another_Type()
    {
        const string source = """
            public static class File
            {
                public static string ReadAllText(string path) => path;
            }

            public sealed class FakeFileTests
            {
                public string Source() => File.ReadAllText("Job.cs");
            }
            """;

        return VerifyAnalyzerAsync(source, TestAssembly, TestFile);
    }

    [Fact]
    public Task Allows_A_Source_File_Read_In_A_Guards_Folder()
    {
        return VerifyAnalyzerAsync(InlineSourceRead, TestAssembly, "/0/Guards/HandlerTests.cs");
    }

    [Fact]
    public Task Allows_A_Source_File_Read_Outside_A_Test_Project()
    {
        return VerifyAnalyzerAsync(InlineSourceRead, "Orbit.Application", TestFile);
    }

    private static Task VerifyAnalyzerAsync(string source, string assemblyName, string filePath)
    {
        var test = new CSharpAnalyzerTest<TestReadsSourceTextAnalyzer, DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
            MarkupOptions = MarkupOptions.UseFirstDescriptor,
        };
        test.TestState.Sources.Add((filePath, source));

        test.SolutionTransforms.Add((solution, projectId) =>
            solution.WithProjectAssemblyName(projectId, assemblyName));

        return test.RunAsync();
    }
}
