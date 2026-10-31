using System.ClientModel;
using System.Diagnostics.CodeAnalysis;
using System.ClientModel.Primitives;
using System.Text.Json;

namespace Orbit.Infrastructure.AI;

internal static class AiProviderErrors
{
    internal static bool IsQuotaCandidate([NotNullWhen(true)] PipelineResponse? response) =>
        response?.Status == 429 && !response.Headers.TryGetValue("Retry-After", out _);

    internal static bool IsQuotaExhausted(Exception exception) =>
        exception is ClientResultException clientException
        && IsQuotaExhausted(clientException.GetRawResponse());

    internal static bool IsQuotaExhausted(PipelineResponse? response)
    {
        if (!IsQuotaCandidate(response))
            return false;

        try
        {
            using var document = JsonDocument.Parse(response.Content);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && (HasQuotaCode(error, "code") || HasQuotaCode(error, "type"));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasQuotaCode(JsonElement error, string field) =>
        error.TryGetProperty(field, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is "insufficient_quota" or "credit_balance_exhausted";
}
