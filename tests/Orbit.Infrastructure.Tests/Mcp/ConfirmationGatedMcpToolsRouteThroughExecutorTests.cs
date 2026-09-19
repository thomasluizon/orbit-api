using System.Text.RegularExpressions;
using FluentAssertions;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Mcp;

/// <summary>
/// Architecture guard behind the MCP confirmation gate. The selective-auth middleware in
/// <c>WebApplicationExtensions</c> forwards a tool whose capability carries a confirmation
/// requirement straight to the tool, because <c>IAgentOperationExecutor</c> owns that gate and is
/// the only layer that receives the caller's confirmation token. That deferral is safe only while
/// two invariants hold: a confirmation requirement always sits on a mutation, and every
/// confirmation-gated MCP tool reaches the executor through <c>McpExecutorBridge</c>. Break either
/// one and a high-risk tool would run with no confirmation at all, so both are pinned here.
/// </summary>
public partial class ConfirmationGatedMcpToolsRouteThroughExecutorTests
{
    private const string BridgeCall = "executorBridge.ExecuteAsync(";

    [GeneratedRegex(@"\[McpServerTool\(Name = ""(?<name>[^""]+)""")]
    private static partial Regex ToolAttributePattern();

    [GeneratedRegex(@"(?m)^(?=    (?:\[|public |private |internal |protected ))")]
    private static partial Regex MemberBoundaryPattern();

    [GeneratedRegex(@"\b(?<name>\w+)\s*\(")]
    private static partial Regex InvocationPattern();

    [Fact]
    public void EveryConfirmationRequirement_SitsOnAMutation()
    {
        var offenders = new AgentCatalogService()
            .GetCapabilities()
            .Where(capability =>
                capability.ConfirmationRequirement is not AgentConfirmationRequirement.None &&
                !capability.IsMutation)
            .Select(capability => capability.Id)
            .ToList();

        offenders.Should().BeEmpty(
            "the MCP middleware defers the confirmation gate to the executor, and only mutations " +
            "are guaranteed to reach it. Offending capability/capabilities: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void EveryConfirmationGatedMcpTool_RoutesThroughTheExecutorBridge()
    {
        var catalogService = new AgentCatalogService();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(LocateMcpToolsDirectory(), "*.cs", SearchOption.TopDirectoryOnly))
        {
            var source = File.ReadAllText(file);
            var memberBodies = BuildMemberBodies(source);
            var toolAttributes = ToolAttributePattern().Matches(source);

            for (var index = 0; index < toolAttributes.Count; index++)
            {
                var toolName = toolAttributes[index].Groups["name"].Value;
                var capability = catalogService.GetCapabilityByMcpTool(toolName);
                if (capability?.ConfirmationRequirement is null or AgentConfirmationRequirement.None)
                    continue;

                var end = index + 1 < toolAttributes.Count ? toolAttributes[index + 1].Index : source.Length;
                var region = source[toolAttributes[index].Index..end];
                if (!ReachesBridge(region, memberBodies))
                    offenders.Add($"{Path.GetFileName(file)}: {toolName} ({capability.ConfirmationRequirement})");
            }
        }

        offenders.Should().BeEmpty(
            "a confirmation-gated MCP tool must reach IAgentOperationExecutor through " +
            "McpExecutorBridge, otherwise nothing enforces its confirmation or step-up. " +
            "Offending tool(s):\n" + string.Join("\n", offenders));
    }

    private static bool ReachesBridge(string region, IReadOnlyDictionary<string, string> memberBodies)
    {
        if (region.Contains(BridgeCall, StringComparison.Ordinal))
            return true;

        return InvocationPattern().Matches(region)
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .Any(callee =>
                memberBodies.TryGetValue(callee, out var body) &&
                body.Contains(BridgeCall, StringComparison.Ordinal));
    }

    private static Dictionary<string, string> BuildMemberBodies(string source)
    {
        var bodies = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var member in MemberBoundaryPattern().Split(source))
        {
            var declaration = InvocationPattern().Match(member);
            if (declaration.Success)
                bodies[declaration.Groups["name"].Value] = member;
        }

        return bodies;
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
