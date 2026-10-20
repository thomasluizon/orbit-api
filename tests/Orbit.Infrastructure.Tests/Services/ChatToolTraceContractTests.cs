using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orbit.Api.Extensions;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;

namespace Orbit.Infrastructure.Tests.Services;

public class ChatToolTraceContractTests
{
    private static readonly IReadOnlyDictionary<string, string> PublicFields = new Dictionary<string, string>
    {
        ["manage_api_keys.key_id"] = "The identifier selects a key to revoke and cannot authenticate a request.",
        ["describe_feature.feature_key"] = "The key selects a public feature explanation.",
        ["get_daily_summary.language"] = "A language code selects the summary language.",
        ["get_retrospective.language"] = "A language code selects the retrospective language.",
        ["create_tag.color"] = "A hexadecimal color code is public display data.",
        ["update_tag.color"] = "A hexadecimal color code is public display data.",
        ["update_notifications.release_other_account"] = "The boolean opts in to device cleanup and contains no credential."
    };

    [Fact]
    public void RegisteredChatTools_MarkCredentialParametersAndResultsSensitive()
    {
        var builder = WebApplication.CreateBuilder();
        builder.AddOrbitAiServices();
        var toolTypes = typeof(IAiTool).Assembly.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract && typeof(IAiTool).IsAssignableFrom(type));
        var dependencies = toolTypes.Append(typeof(BulkHabitReplayPlanner))
            .SelectMany(type => type.GetConstructors())
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .Where(type => type.IsInterface).Distinct();
        foreach (var type in dependencies)
            builder.Services.AddSingleton(type, Substitute.For([type], []));
        using var provider = builder.Services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var tools = scope.ServiceProvider.GetRequiredService<AiToolRegistry>().GetAll();

        tools.Should().NotBeEmpty();
        tools.Select(tool => tool.Name).Should().Contain("ask").And.Contain("update_notifications").And.Contain("manage_api_keys");
        tools.Select(tool => tool.Name).Should().Contain("update_checklist_template").And.Contain("update_profile_preferences")
            .And.Contain("manage_calendar_sync").And.Contain("bulk_log_habits");
        foreach (var tool in tools)
        {
            AssertSensitiveFields(JsonSerializer.SerializeToElement(tool.GetParameterSchema()), tool.Name);
            if (tool.GetResultSchema() is { } resultSchema)
                AssertSensitiveFields(JsonSerializer.SerializeToElement(resultSchema), tool.Name);
        }
    }

    private static void AssertSensitiveFields(JsonElement schema, string path)
    {
        if (schema.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in schema.EnumerateArray())
                AssertSensitiveFields(item, path);
            return;
        }
        if (schema.ValueKind != JsonValueKind.Object)
            return;
        foreach (var entry in schema.EnumerateObject())
        {
            if (entry.Name != "properties")
            {
                AssertSensitiveFields(entry.Value, path);
                continue;
            }
            foreach (var field in entry.Value.EnumerateObject())
            {
                var fieldPath = $"{path}.{field.Name}";
                var description = field.Value.TryGetProperty("description", out var text) ? text.GetString() : null;
                if (ReadsAsCredential($"{field.Name} {description}") && !PublicFields.ContainsKey(fieldPath))
                    (field.Value.TryGetProperty("sensitive", out var sensitive) && sensitive.ValueKind == JsonValueKind.True)
                        .Should().BeTrue($"{fieldPath} carries a credential and must be omitted from evaluation traces");
                AssertSensitiveFields(field.Value, fieldPath);
            }
        }
    }

    private static bool ReadsAsCredential(string text)
    {
        var words = Regex.Replace(text, "([a-z0-9])([A-Z])", "$1 $2").Replace('_', ' ').Replace('-', ' ');
        return Regex.IsMatch(words,
            @"\b(auth(?:entication|orization)?|tokens?|secrets?|keys?|codes?|passwords?|endpoints?|p256dh|credentials?|encrypted|system\s*prompt|conversation)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
