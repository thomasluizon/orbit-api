using System.Text.Json;
using Orbit.Domain.Models;

namespace Orbit.Api.Controllers;

/// <summary>
/// Reads the optional <c>clientContext</c> query parameter: a JSON object in the form of
/// <see cref="AgentClientContext"/>. A missing or blank value is no client context.
/// </summary>
internal static class ClientContextQuery
{
    /// <summary>False when the value is not a JSON object in the form of a client context.</summary>
    public static bool TryParse(string? value, out AgentClientContext? context)
    {
        context = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        try
        {
            context = JsonSerializer.Deserialize<AgentClientContext>(value, JsonSerializerOptions.Web);
            return context is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
