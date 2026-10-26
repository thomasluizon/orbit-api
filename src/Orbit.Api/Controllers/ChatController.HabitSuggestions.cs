using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Queries;
using Orbit.Domain.Models;

namespace Orbit.Api.Controllers;

public partial class ChatController
{
    [HttpPost("habit-suggestions/refine")]
    [ProducesResponseType<HabitSuggestionDraft>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RefineHabitSuggestion([FromBody] RefineHabitSuggestionRequest request, CancellationToken ct)
    {
        if (!CanAccessChat() || HttpContext.User.GetAgentAuthMethod() != AgentAuthMethod.Jwt) return Forbid();
        var result = await mediator.Send(new RefineHabitSuggestionQuery(HttpContext.GetUserId(), request.Draft,
            request.Instruction, request.Language), ct);
        return result.ToPayGateAwareResult(value => Ok(value));
    }

    [HttpPost("habit-suggestions/preview")]
    [ProducesResponseType<AgentExecuteOperationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> PreviewHabitSuggestion([FromBody] PreviewHabitSuggestionRequest request, CancellationToken ct)
    {
        if (!CanAccessChat() || HttpContext.User.GetAgentAuthMethod() != AgentAuthMethod.Jwt) return Forbid();
        var result = await mediator.Send(new PreviewHabitSuggestionCommand(HttpContext.GetUserId(), request.Draft,
            request.Language, HttpContext.User.GetAgentAuthMethod(), HttpContext.User.GetGrantedAgentScopes(),
            HttpContext.User.IsReadOnlyCredential()), ct);
        return result.ToPayGateAwareResult(value => Ok(value));
    }
}
