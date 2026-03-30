using System.ComponentModel;
using System.Security.Claims;
using MediatR;
using ModelContextProtocol.Server;
using Orbit.Application.Profile.Queries;

namespace Orbit.Api.Mcp.Tools;

[McpServerToolType]
public class ProfileTools(IMediator mediator)
{
    [McpServerTool(Name = "get_profile"), Description("Get the authenticated user's profile information.")]
    public async Task<string> GetProfile(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var query = new GetProfileQuery(userId);
        var result = await mediator.Send(query, cancellationToken);

        if (result.IsFailure)
            return $"Error: {result.Error}";

        var p = result.Value;
        return $"Name: {p.Name}\n" +
               $"Email: {p.Email}\n" +
               $"Plan: {p.Plan}{(p.HasProAccess ? " (Pro)" : "")}\n" +
               (p.IsTrialActive ? $"Trial ends: {p.TrialEndsAt:yyyy-MM-dd}\n" : "") +
               (p.TimeZone is not null ? $"Timezone: {p.TimeZone}\n" : "") +
               (p.Language is not null ? $"Language: {p.Language}\n" : "") +
               $"AI Messages: {p.AiMessagesUsed}/{p.AiMessagesLimit}\n" +
               $"Level: {p.Level} ({p.LevelTitle}) - {p.TotalXp} XP\n" +
               $"Week starts on: {(p.WeekStartDay == 0 ? "Sunday" : "Monday")}";
    }

    private static Guid GetUserId(ClaimsPrincipal user)
    {
        var claim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new UnauthorizedAccessException("User ID not found in token");
        return Guid.Parse(claim);
    }
}
