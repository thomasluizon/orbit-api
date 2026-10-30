using System.Text.Json;
using System.Text.Json.Serialization;
using Orbit.Application.Chat.FeatureExplanations;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Tools.Implementations;

public sealed record FeatureHelpResult(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("related_capabilities")] IReadOnlyList<string> RelatedCapabilities,
    [property: JsonPropertyName("related_surfaces")] IReadOnlyList<string> RelatedSurfaces,
    [property: JsonPropertyName("markdown")] string Markdown,
    [property: JsonPropertyName("answer_scope")] string AnswerScope,
    [property: JsonPropertyName("app_surfaces"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<AppSurface>? AppSurfaces = null);

public class DescribeFeatureTool(IFeatureExplanationService features, IAppSurfaceCatalog surfaces) : IAiTool
{
    private const string AnswerScope = "Use only mechanics, screens, fields and steps explicitly documented in markdown or app_surfaces. "
        + "related_surfaces lists destinations, not editing instructions. "
        + "app_surfaces comes from the same catalog as list_app_surfaces_v2; use only its documented destinations and steps. "
        + "Never infer a habit edit screen or creation workflow from a related destination. "
        + "When the requested detail is absent, say that the explanation does not cover it; do not invent navigation or settings. "
        + "Answer in the person's language. Use at most three short steps, one complete sentence per line, at most 180 characters per line. Do not join steps into a paragraph.";

    public string Name => "describe_feature";
    public bool IsReadOnly => true;

    public string Description =>
        "Read authoritative rules before explaining streaks, freezes, frequencies, XP/levels, notifications, paygate or schedule math. " + AnswerScope;

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            feature_key = new
            {
                type = JsonSchemaTypes.String,
                description = "Which Orbit feature to explain.",
                @enum = JsonSchemaTypes.FeatureKeyEnum
            }
        },
        required = new[] { "feature_key" }
    };

    public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var featureKey = JsonArgumentParser.GetOptionalString(args, "feature_key");
        if (string.IsNullOrWhiteSpace(featureKey))
            return Task.FromResult(new ToolResult(false, Error: "feature_key is required."));

        var explanation = features.Get(featureKey);
        if (explanation is null)
            return Task.FromResult(new ToolResult(false, Error: $"Unknown feature '{featureKey}'."));

        return Task.FromResult(new ToolResult(true, Payload: new FeatureHelpResult(explanation.Key,
            explanation.DisplayName, explanation.RelatedCapabilities, explanation.RelatedSurfaces, explanation.Body, AnswerScope,
            surfaces.GetSurfaces().Where(surface => explanation.RelatedSurfaces.Contains(surface.Id, StringComparer.Ordinal)).ToArray())));
    }
}
