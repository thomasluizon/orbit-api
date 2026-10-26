using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Queries;
using Orbit.Domain.Models;

namespace Orbit.Api.Controllers;

public partial class ChatController
{
    [HttpPost("habit-month/refresh")]
    [ProducesResponseType<HabitMonthReadBlockRefreshResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    public async Task<IActionResult> RefreshHabitMonthReadBlock([FromBody] RefreshHabitMonthReadBlockRequest request,
        CancellationToken cancellationToken)
    {
        if (!CanAccessChat()
            || HttpContext.User.GetAgentAuthMethod() is AgentAuthMethod.ApiKey or AgentAuthMethod.OAuth
                && !HttpContext.User.GetGrantedAgentScopes().Contains(AgentScopes.ReadHabits, StringComparer.OrdinalIgnoreCase))
            return Forbid();

        var result = await mediator.Send(new RefreshReadBlockQuery(HttpContext.GetUserId(), request.Token,
            request.ClientContext, AuthMethod: HttpContext.User.GetAgentAuthMethod(),
            GrantedScopes: HttpContext.User.GetGrantedAgentScopes(), ReadOnlyCredential: HttpContext.User.IsReadOnlyCredential(),
            HabitMonthOnly: true), cancellationToken);
        return result.ToPayGateAwareResult(value => Ok(HabitMonthReadBlockRefreshResponse.From(value)));
    }
}
