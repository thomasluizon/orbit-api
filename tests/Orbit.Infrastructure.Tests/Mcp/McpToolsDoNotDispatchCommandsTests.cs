using System.Text.RegularExpressions;
using FluentAssertions;

namespace Orbit.Infrastructure.Tests.Mcp;

/// <summary>
/// Architecture guard: MCP tools route MUTATIONS through <c>McpExecutorBridge</c> →
/// <c>IAgentOperationExecutor</c> for uniform policy evaluation and audit. Dispatching a Command
/// (mutation) via <c>mediator.Send</c> from an MCP tool bypasses that layer, so it is banned. Read
/// queries (<c>mediator.Send(new *Query(...))</c>) are permitted and stay on MediatR by design.
/// </summary>
public partial class McpToolsDoNotDispatchCommandsTests
{
    [GeneratedRegex(@"\b\w+\s*\.\s*Send\s*\(\s*(new\s+(?:\w+\.)*\w*Command\b|\w*[Cc]ommand)\b", RegexOptions.Singleline)]
    private static partial Regex CommandDispatchPattern();

    [Fact]
    public void McpTools_DoNotDispatchCommandsThroughMediator()
    {
        var toolsDirectory = LocateMcpToolsDirectory();
        var offenders = FindCommandDispatches(toolsDirectory);

        offenders.Should().BeEmpty(
            "MCP tools must route mutations through McpExecutorBridge; dispatching a *Command via " +
            "mediator.Send bypasses policy evaluation and audit. Offending call-site(s):\n" +
            string.Join("\n", offenders));
    }

    [Theory]
    [InlineData("sender.Send(\n new FooCommand(value));", false)]
    [InlineData("mediator.Send(\n new FooCommand(value));", false)]
    [InlineData("sender.Send(\n new FooCommand(value));", true)]
    [InlineData("mediator.Send(\n new FooCommand(value));", true)]
    public void Guard_DetectsCommandDispatchAcrossLinesAndSenderNames(string source, bool nested)
    {
        var root = Path.Combine(Path.GetTempPath(), "orbit-mcp-guard-" + Guid.NewGuid().ToString("N"));
        var directory = nested ? Path.Combine(root, "Nested") : root;
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "Tool.cs"), source);
            FindCommandDispatches(root).Should().ContainSingle();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string[] FindCommandDispatches(string directory) =>
        Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(file => CommandDispatchPattern().IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(directory, file)).ToArray();

    private static string LocateMcpToolsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Orbit.Api", "Mcp");
            if (Directory.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate src/Orbit.Api/Mcp by walking up from the test output directory.");
    }
}
