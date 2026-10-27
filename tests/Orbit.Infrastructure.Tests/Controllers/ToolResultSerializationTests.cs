using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using JsonOptions = Microsoft.AspNetCore.Mvc.JsonOptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Controllers;

public sealed class ToolResultSerializationTests
{
    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    public void OperationAccess_IsOptionalAndAdditiveInHttpAndStreamResponses(string access)
    {
        var builder = WebApplication.CreateBuilder();
        Orbit.Api.Extensions.ServiceCollectionExtensions.AddApiPipeline(builder);
        using var provider = builder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
        var legacy = new AgentOperationResult("test", "test", AgentRiskClass.Low,
            AgentConfirmationRequirement.None, AgentOperationStatus.Failed);
        var legacyJson = JsonSerializer.Serialize(legacy, options);
        var current = legacy with { Access = access };
        var currentJson = JsonSerializer.Serialize(current, options);
        JsonSerializer.Deserialize<AgentOperationResult>(legacyJson, options).Should().Be(legacy);
        JsonSerializer.Deserialize<AgentOperationResult>(currentJson, options).Should().Be(current);
        var node = System.Text.Json.Nodes.JsonNode.Parse(currentJson)!.AsObject();
        node["access"]!.GetValue<string>().Should().Be(access);
        node.Remove("access").Should().BeTrue();
        node.ToJsonString(options).Should().Be(legacyJson);

        using var json = JsonDocument.Parse(ChatStreamEvent.Final(new ChatResponse(null, [], [current, legacy])).ToJson());
        var operations = json.RootElement.GetProperty("response").GetProperty("operations");
        operations[0].GetProperty("access").GetString().Should().Be(access);
        operations[1].TryGetProperty("access", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("openapi.json")]
    [InlineData("openapi_v2.json")]
    public void ParamsSchema_NumericFieldsOnlyAdvertiseIntegers(string file)
    {
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..", "src/Orbit.Api"));
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, file)));
        var properties = document.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty("ToolResultMessageParams").GetProperty("properties");

        foreach (var field in new[] { "count", "totalCount", "skippedCount", "weekStartDay", "hours" })
        {
            var property = properties.GetProperty(field);
            property.GetProperty("type").EnumerateArray().Select(type => type.GetString())
                .Should().BeEquivalentTo("integer", "null");
            property.TryGetProperty("pattern", out _).Should().BeFalse();
        }
    }

    [Fact]
    public void ActionContract_NonStreamSerializerPreservesLegacyAndWritesNumericParams()
    {
        var builder = WebApplication.CreateBuilder();
        Orbit.Api.Extensions.ServiceCollectionExtensions.AddApiPipeline(builder);
        using var provider = builder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
        var legacy = new ActionResult("CreateHabit", ActionStatus.Success,
            Guid.Parse("11111111-1111-1111-1111-111111111111"), "Read");
        const string expected = """{"type":"CreateHabit","status":"Success","entityId":"11111111-1111-1111-1111-111111111111","entityName":"Read","error":null,"field":null,"suggestedSubHabits":null,"clarificationRequest":null}""";
        JsonSerializer.Serialize(legacy, options).Should().Be(expected);
        var card = legacy with
        {
            MessageKey = "chat.toolResult.updatedEmojis",
            Params = new ToolResultMessageParams(Count: 1, TotalCount: 2, SkippedCount: 1, WeekStartDay: 0, Hours: 24)
        };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(card, options));
        json.RootElement.GetProperty("params").GetRawText().Should()
            .Be("""{"count":1,"totalCount":2,"skippedCount":1,"weekStartDay":0,"hours":24}""");
        JsonSerializer.Serialize(card with { MessageKey = null, Params = null }, options).Should().Be(expected);
        JsonSerializer.Deserialize<ActionResult>(expected, options).Should().Be(legacy);
        JsonSerializer.Deserialize<ActionResult>(JsonSerializer.Serialize(card, options), options).Should().Be(card);
    }
}
