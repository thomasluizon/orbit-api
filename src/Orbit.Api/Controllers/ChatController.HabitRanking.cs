using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Models;

namespace Orbit.Api.Controllers;

public sealed record HabitRankingRequest(DateOnly? DateFrom = null, DateOnly? DateToExclusive = null,
    string? Window = null, int? Months = null, string? Metric = null,
    IReadOnlyList<Guid>? HabitIds = null, int? PageSize = null);

public partial class ChatController
{
    [HttpPost("habit-ranking/query")]
    [ProducesResponseType<HabitRankingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> QueryHabitRanking([FromBody] HabitRankingRequest request, CancellationToken cancellationToken)
    {
        if (!CanReadHabitRanking())
            return Forbid();
        var result = await mediator.Send(new GetHabitRankingQuery(HttpContext.GetUserId(), request.DateFrom,
            request.DateToExclusive, request.Window, request.Months, request.Metric, request.HabitIds,
            request.PageSize), cancellationToken);
        return result.ToPayGateAwareResult(value => Ok(value));
    }

    [HttpGet("habit-ranking")]
    [ProducesResponseType<HabitRankingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> GetHabitRankingPage([FromQuery] string queryToken,
        [FromQuery] string? cursor = null, [FromQuery] bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (!CanReadHabitRanking())
            return Forbid();
        var result = await mediator.Send(new GetHabitRankingQuery(HttpContext.GetUserId(), QueryToken: queryToken,
            Cursor: cursor, Refresh: refresh), cancellationToken);
        return result.ToPayGateAwareResult(value => Ok(value));
    }

    private bool CanReadHabitRanking() => CanAccessChat()
        && (HttpContext.User.GetAgentAuthMethod() is not (AgentAuthMethod.ApiKey or AgentAuthMethod.OAuth)
            || HttpContext.User.GetGrantedAgentScopes().Contains(AgentScopes.ReadHabits, StringComparer.OrdinalIgnoreCase));
}
