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
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(toolsDirectory, "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            if (CommandDispatchPattern().IsMatch(source))
                offenders.Add(Path.GetRelativePath(toolsDirectory, file));
        }

        offenders.Should().BeEmpty(
            "MCP tools must route mutations through McpExecutorBridge; dispatching a *Command via " +
            "mediator.Send bypasses policy evaluation and audit. Offending call-site(s):\n" +
            string.Join("\n", offenders));
    }

    [Theory]
    [InlineData("sender.Send(\n new FooCommand(value));")]
    [InlineData("mediator.Send(\n new FooCommand(value));")]
    public void Guard_DetectsCommandDispatchAcrossLinesAndSenderNames(string source) =>
        CommandDispatchPattern().IsMatch(source).Should().BeTrue();

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
