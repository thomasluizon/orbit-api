using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Orbit.Api.Controllers;
using Orbit.Domain.Models;

namespace Orbit.Api.OpenApi;

/// <summary>
/// Keeps the published component schema name of each type whose C# name no longer matches it.
/// A component schema name is a wire name, so an internal rename must not change it.
/// </summary>
internal static class PublishedSchemaNames
{
    private static readonly Dictionary<Type, string> Pinned = new()
    {
        [typeof(AiBlockAnswer)] = "AstraBlockAnswer",
        [typeof(AiNextAction)] = "AstraNextAction",
        [typeof(AiPlan)] = "AstraPlan",
        [typeof(AiPlanStep)] = "AstraPlanStep",
        [typeof(AiQuestion)] = "AstraQuestion",
        [typeof(AiQuestionAnswer)] = "AstraQuestionAnswer",
        [typeof(AiQuestionBlock)] = "AstraQuestionBlock",
        [typeof(AiQuestionOption)] = "AstraQuestionOption",
        [typeof(AiQuestionProgress)] = "AstraQuestionProgress",
        [typeof(ProfileController.SetProactiveAiEnabledRequest)] = "SetProactiveAstraEnabledRequest",
    };

    public static string? ReferenceId(JsonTypeInfo typeInfo) =>
        Pinned.TryGetValue(typeInfo.Type, out var name) ? name : OpenApiOptions.CreateDefaultSchemaReferenceId(typeInfo);

    public static string ComponentName(Type type) => Pinned.GetValueOrDefault(type, type.Name);
}
