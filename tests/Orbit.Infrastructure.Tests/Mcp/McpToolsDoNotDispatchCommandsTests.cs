using System.Text.RegularExpressions;
using FluentAssertions;

namespace Orbit.Infrastructure.Tests.Mcp;

/// <summary>
/// Architecture guard: MCP tools route MUTATIONS through <c>McpExecutorBridge</c> →
/// <c>IAgentOperationExecutor</c> for uniform policy evaluation and audit. Dispatching a Command
/// (mutation) via <c>mediator.Send</c> from an MCP tool bypasses that layer, so it is banned. Read
/// queries (<c>mediator.Send(new *Query(...))</c>) are permitted and stay on MediatR by design.
/// </summary>
public class McpToolsDoNotDispatchCommandsTests
{
    // Matches mediator.Send( ... ) where the dispatched argument is a Command — either an inline
    // `new SomethingCommand` or a local whose name ends in `Command`/equals `command`. Spans newlines.
    private static readonly Regex CommandDispatchPattern = new(
        @"mediator\s*\.\s*Send\s*\(\s*(new\s+\w*Command\b|\w*[Cc]ommand)\b",
        RegexOptions.Compiled | RegexOptions.Singleline);

    [Fact]
    public void McpTools_DoNotDispatchCommandsThroughMediator()
    {
        var toolsDirectory = LocateMcpToolsDirectory();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(toolsDirectory, "*.cs", SearchOption.TopDirectoryOnly))
        {
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                if (CommandDispatchPattern.IsMatch(lines[index]))
                    offenders.Add($"{Path.GetFileName(file)}:{index + 1}: {lines[index].Trim()}");
            }
        }

        offenders.Should().BeEmpty(
            "MCP tools must route mutations through McpExecutorBridge; dispatching a *Command via " +
            "mediator.Send bypasses policy evaluation and audit. Offending call-site(s):\n" +
            string.Join("\n", offenders));
    }

    private static string LocateMcpToolsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Orbit.Api", "Mcp", "Tools");
            if (Directory.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate src/Orbit.Api/Mcp/Tools by walking up from the test output directory.");
    }
}
