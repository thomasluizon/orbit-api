using System.Text.Json;
using Orbit.Application.Chat.FeatureExplanations;

namespace Orbit.Application.Chat.Tools.Implementations;

public class DescribeFeatureTool(IFeatureExplanationService features) : IAiTool
{
    public string Name => "describe_feature";
    public bool IsReadOnly => true;

    public string Description =>
        "Read authoritative rules before explaining streaks, freezes, frequencies, XP/levels, notifications, paygate or schedule math.";

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

        return Task.FromResult(new ToolResult(true, Payload: new
        {
            key = explanation.Key,
            display_name = explanation.DisplayName,
            related_capabilities = explanation.RelatedCapabilities,
            related_surfaces = explanation.RelatedSurfaces,
            markdown = explanation.Body
        }));
    }
}
