using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Controllers;
using Orbit.Domain.Models;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Controllers;

public class AiMemoryRemovalTests
{
    [Fact]
    public void MemoryRoutesAndCapabilitiesAreAbsent()
    {
        typeof(ProfileController).Assembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type))
            .Select(type => type.Name).Should().NotContain("UserFactsController");
        typeof(ProfileController).GetMethod("SetAiMemory").Should().BeNull();

        var catalog = new AgentCatalogService();
        catalog.GetCapabilities().Select(capability => capability.Id)
            .Should().NotContain(["user-facts.read", "user-facts.delete", "profile.ai-memory.write"])
            .And.Contain([AgentCapabilityIds.ProfileReadBasic, AgentCapabilityIds.ProfileAiSummaryWrite,
                AgentCapabilityIds.ProfileProactiveAstraWrite]);
        foreach (var operation in new[] { "get_user_facts", "delete_user_facts", "delete_user_fact", "set_ai_memory" })
            catalog.GetOperation(operation).Should().BeNull();
        typeof(IPayGateService).GetMethods().Select(method => method.Name)
            .Should().NotContain(["CanManageAiMemory", "CanReadUserFacts", "CanManageUserFacts"]);
    }

    [Theory]
    [InlineData("read_user_facts")]
    [InlineData("delete_user_facts")]
    public void RetiredScopesCannotGrantCapabilities(string scope)
    {
        var scopeSets = typeof(AgentScopes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => typeof(IEnumerable<string>).IsAssignableFrom(field.FieldType));
        foreach (var field in scopeSets)
            ((IEnumerable<string>)field.GetValue(null)!).Should().NotContain(scope);
        new AgentCatalogService().GetCapabilities().Should().NotContain(capability => capability.Scope == scope);
    }

    [Theory]
    [InlineData("openapi.json")]
    [InlineData("openapi_v2.json")]
    public void GeneratedContractHasNoMemoryRoutesAndRetainsProfileCompatibility(string file)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../..", "src/Orbit.Api", file));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        root.GetProperty("paths").EnumerateObject().Select(item => item.Name)
            .Should().NotContain(route => route.StartsWith("/api/user-facts", StringComparison.OrdinalIgnoreCase)
                || route.EndsWith("/ai-memory", StringComparison.OrdinalIgnoreCase));
        var profile = root.GetProperty("components").GetProperty("schemas").GetProperty("ProfileResponse");
        profile.GetProperty("properties").GetProperty("aiMemoryEnabled")
            .GetProperty("type").GetString().Should().Be("boolean");
        profile.GetProperty("required").EnumerateArray().Select(item => item.GetString())
            .Should().Contain("aiMemoryEnabled");
    }
}
