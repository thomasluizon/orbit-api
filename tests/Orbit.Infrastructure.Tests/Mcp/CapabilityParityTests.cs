using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;
using ServiceCollectionExtensions = Orbit.Api.Extensions.ServiceCollectionExtensions;

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
    public void EveryDeclaredMcpTool_IsRegisteredByProductionBootstrap()
    {
        var builder = WebApplication.CreateBuilder();
        var registerTools = typeof(ServiceCollectionExtensions).GetMethod(
            "AddMcpToolServer", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("The production MCP registration method was not found.");
        registerTools.Invoke(null, [builder]);
        using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<McpServerOptions>>().Value;
        var registeredTools = options.ToolCollection
            ?? throw new InvalidOperationException("The production MCP tool collection was not configured.");
        var registeredNames = registeredTools.Select(tool => tool.ProtocolTool.Name).ToHashSet(StringComparer.Ordinal);
        var declaredNames = _catalogService.GetCapabilities()
            .SelectMany(capability => capability.McpToolNames ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        declaredNames.Should().NotBeEmpty();
        declaredNames.Except(registeredNames, StringComparer.Ordinal).Should().BeEmpty(
            "every MCP tool declared in the capability catalog must be registered by the production bootstrap");
    }

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
