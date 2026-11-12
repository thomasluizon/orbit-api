using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using ModelContextProtocol.Server;
using Orbit.Application.Common;
using Orbit.Application.Profile.Models;
using Orbit.Application.Profile.Queries;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Tests.AI;

namespace Orbit.Infrastructure.Tests.Contracts;

/// <summary>
/// Pins every name that an installed client, a stored row or an alert reads by its exact text.
/// A rename of an internal identifier must leave each of these values as it is.
/// </summary>
public class WireNameTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static string[] JsonNames<T>() where T : class
    {
        using var document = JsonSerializer.SerializeToDocument((T)RuntimeHelpers.GetUninitializedObject(typeof(T)), Web);
        return document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
    }

    [Fact]
    public void ProfileResponse_KeepsTheTwoLegacyJsonNames()
    {
        JsonNames<ProfileResponse>().Should().Contain("hasTriedAstra").And.Contain("proactiveAstraEnabled");
    }

    [Fact]
    public void ExportedSettings_KeepsEveryJsonName()
    {
        JsonNames<ExportedSettings>().Should().BeEquivalentTo(
            "timeZone", "language", "weekStartDay", "themePreference", "colorScheme", "aiSummaryEnabled",
            "proactiveAstraEnabled", "uses24HourClock", "storedProactiveAstraEnabled", "mascotMotionEnabled");
    }

    [Fact]
    public void EfModel_KeepsTheTableAndColumnNames()
    {
        using var db = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);

        var blocks = db.Model.GetEntityTypes().Single(entity => entity.ClrType.Name is "IssuedAstraBlock" or "IssuedAiBlock");
        blocks.GetTableName().Should().Be("IssuedAstraBlocks");
        var users = db.Model.FindEntityType(typeof(User))!;
        var table = StoreObjectIdentifier.Table("Users", users.GetSchema());
        var columns = users.GetProperties().Select(property => property.GetColumnName(table)).ToArray();
        columns.Should().Contain(["ProactiveAstraEnabled", "HasTriedAstra"]);
        columns.Should().NotContain(["ProactiveAiEnabled", "HasTriedAi"]);
        var blockTable = StoreObjectIdentifier.Table("IssuedAstraBlocks", blocks.GetSchema());
        blocks.GetProperties().Select(property => property.GetColumnName(blockTable)).Should().BeEquivalentTo(
            "Id", "UserId", "Form", "DefinitionJson", "CreatedAtUtc", "ExpiresAtUtc", "AnsweredAtUtc",
            "InvalidatedAtUtc", "AnswerJson", "ResponseJson", "CommittedResponseJson", "ReservedAllowanceDate");
    }

    [Fact]
    public void Routes_KeepTheProactivePathsAndTheProfilePath()
    {
        var controllers = typeof(Orbit.Api.Controllers.ChatController).Assembly.GetTypes();
        var proactive = controllers.Single(type => type.Name is "ProactiveAstraController" or "ProactiveAiController");
        proactive.GetCustomAttribute<Microsoft.AspNetCore.Mvc.RouteAttribute>()!.Template.Should().Be("api/ai/proactive");
        proactive.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(method => method.GetCustomAttributes<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>())
            .Select(attribute => attribute.HttpMethods.Single() + " " + attribute.Template)
            .Should().BeEquivalentTo("GET current", "GET {id:guid}", "POST {id:guid}/opened", "POST {id:guid}/acted");
        typeof(Orbit.Api.Controllers.ProfileController).GetMethods()
            .SelectMany(method => method.GetCustomAttributes<Microsoft.AspNetCore.Mvc.HttpPutAttribute>())
            .Select(attribute => attribute.Template).Should().Contain("proactive-astra");
    }

    [Fact]
    public void ErrorCodes_KeepTheSixteenBlockStrings()
    {
        ConstantValues(typeof(ErrorCodes), "ASTRA_BLOCK_").Should().BeEquivalentTo(
            "ASTRA_BLOCK_ID_REQUIRED", "ASTRA_BLOCK_SELECTION_REQUIRED", "ASTRA_BLOCK_INVALID_SELECTION",
            "ASTRA_BLOCK_FREE_TEXT_TOO_LONG", "ASTRA_BLOCK_UNKNOWN_SELECTION", "ASTRA_BLOCK_SINGLE_SELECTION",
            "ASTRA_BLOCK_EMPTY_SELECTION", "ASTRA_BLOCK_FREE_TEXT_DISABLED", "ASTRA_BLOCK_EMPTY_ANSWER",
            "ASTRA_BLOCK_PREVIEW_UNAVAILABLE", "ASTRA_BLOCK_INVALID_JSON", "ASTRA_BLOCK_INVALID_DEFINITION",
            "ASTRA_BLOCK_NOT_FOUND", "ASTRA_BLOCK_INVALIDATED", "ASTRA_BLOCK_EXPIRED", "ASTRA_BLOCK_ANSWERED");
    }

    [Fact]
    public void FeatureFlagKeys_KeepTheLegacyFlagValues()
    {
        ConstantValues(typeof(FeatureFlagKeys), "astra_").Should().BeEquivalentTo(
            "astra_eval_trace", "astra_eval_unmetered", "astra_block_first", "astra_conversation_recording",
            "astra_ask_blocks_disabled", "astra_proactive_engine_disabled", "astra_crisis_resources_disabled",
            "astra_change_preview_disabled", "astra_tool_steps_disabled", "astra_parts_disabled",
            "astra_follow_ups_disabled", "astra_period_blocks_disabled", "astra_goal_projections_disabled",
            "astra_status_blocks_disabled", "astra_record_lists_disabled", "astra_account_rows_disabled");
        ConstantValues(typeof(FeatureFlagKeys), "orbit_").Should().Equal("orbit_guided_flows");
        ConstantValues(typeof(FeatureFlagKeys), "phrase_").Should().Equal("phrase_reader_ai_disabled");
        ConstantValues(typeof(AppConfigKeys), "Astra").Should().Equal("AstraGroundingEnforce");
    }

    [Fact]
    public void ToolNames_EqualTheSnapshotAndHoldTheProactiveTool()
    {
        using var snapshot = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Contracts", "tool-names.json")));
        var expectedChat = snapshot.RootElement.GetProperty("chat").EnumerateArray().Select(name => name.GetString()!).ToArray();
        var expectedMcp = snapshot.RootElement.GetProperty("mcp").EnumerateArray().Select(name => name.GetString()!).ToArray();

        var chat = AiInputBudgetTests.RegisteredTools().Select(tool => tool.Name).ToArray();
        var mcp = typeof(Orbit.Api.Controllers.ChatController).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .OfType<string>().ToArray();

        chat.Should().BeEquivalentTo(expectedChat).And.HaveCount(71).And.Contain("set_proactive_astra");
        mcp.Should().BeEquivalentTo(expectedMcp).And.HaveCount(94).And.Contain("set_proactive_astra");
    }

    private static string[] ConstantValues(Type type, string prefix) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Where(value => value.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
}
