using FluentAssertions;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Mcp;

/// <summary>
/// Guards chat ↔ MCP tool parity: every domain capability that exposes one surface must
/// expose the other, so the two AI entry points stay feature-equivalent. Capabilities that
/// are intentionally single-surface (or surfaced only through controllers/prompt context)
/// are listed in <see cref="OneSidedExemptions"/> with a rationale.
/// </summary>
public class CapabilityParityTests
{
    private readonly AgentCatalogService _catalogService = new();

    private static readonly HashSet<string> OneSidedExemptions = new(StringComparer.OrdinalIgnoreCase)
    {
        AgentCapabilityIds.ChatInteract,
        AgentCapabilityIds.CatalogCapabilitiesRead,
        AgentCapabilityIds.CatalogDataRead,
        AgentCapabilityIds.CatalogSurfacesRead,
    };

    [Fact]
    public void EveryCapability_HasMatchingChatAndMcpSurfaces()
    {
        var parityGaps = new List<string>();

        foreach (var capability in _catalogService.GetCapabilities())
        {
            if (OneSidedExemptions.Contains(capability.Id))
                continue;

            var hasChat = capability.ChatToolNames is { Count: > 0 };
            var hasMcp = capability.McpToolNames is { Count: > 0 };

            if (hasChat == hasMcp)
                continue;

            var missingSurface = hasChat ? "mcpTools" : "chatTools";
            parityGaps.Add($"{capability.Id} (missing {missingSurface})");
        }

        parityGaps.Should().BeEmpty(
            "every capability must expose both chat and MCP tools, or be listed in OneSidedExemptions with a rationale");
    }
}
