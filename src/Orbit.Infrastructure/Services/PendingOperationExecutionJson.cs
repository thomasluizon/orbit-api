using System.Text.Json;
using System.Text.Json.Serialization;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// Writes an executed operation in the same JSON shape that the execute response sends to the client.
/// The MVC options use the web defaults and a <see cref="JsonStringEnumConverter"/>, so this writer uses the same options.
/// </summary>
internal static class PendingOperationExecutionJson
{
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Keeps the full result when it fits the stored budget. Removes the payload and the read block first,
    /// then the free text, so the status and the target ids always stay.
    /// </summary>
    public static string Serialize(AgentOperationResult operation)
    {
        var json = JsonSerializer.Serialize(operation, WireOptions);
        if (json.Length <= PendingAgentOperationState.MaxExecutionJsonLength) return json;
        json = JsonSerializer.Serialize(operation with { Payload = null, ReadBlock = null }, WireOptions);
        if (json.Length <= PendingAgentOperationState.MaxExecutionJsonLength) return json;
        return JsonSerializer.Serialize(operation with
        {
            Payload = null,
            ReadBlock = null,
            Summary = null,
            TargetName = null,
            PolicyReason = null
        }, WireOptions);
    }
}
