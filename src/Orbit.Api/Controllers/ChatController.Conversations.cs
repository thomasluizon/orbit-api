using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Queries;

namespace Orbit.Api.Controllers;

public partial class ChatController
{
    [HttpGet("conversations/latest")]
    [ProducesResponseType<ConversationPage>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetLatestConversation(CancellationToken cancellationToken,
        [FromQuery] Guid? conversationId = null, [FromQuery] long? beforeOrder = null, [FromQuery] int pageSize = 40)
    {
        if (!CanAccessChat()) return Forbid();
        var result = await mediator.Send(new GetLatestConversationQuery(HttpContext.GetUserId(), conversationId, beforeOrder, pageSize), cancellationToken);
        return result.ToPayGateAwareResult(page => Ok(page));
    }

    [HttpGet("conversations/{conversationId:guid}/images/{turnId:guid}")]
    [ProducesResponseType(typeof(byte[]), StatusCodes.Status200OK, "image/png", "image/jpeg", "image/webp")]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetConversationImage(Guid conversationId, Guid turnId, CancellationToken cancellationToken)
    {
        if (!CanAccessChat()) return Forbid();
        var result = await mediator.Send(new GetConversationImageQuery(HttpContext.GetUserId(), conversationId, turnId), cancellationToken);
        Response.Headers.CacheControl = "private, no-store";
        return result.ToPayGateAwareResult(image => File(image.Data, image.ContentType));
    }

    [HttpDelete("conversations/{conversationId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteConversation(Guid conversationId, CancellationToken cancellationToken)
    {
        if (!CanAccessChat() || HttpContext.User.IsReadOnlyCredential()) return Forbid();
        var result = await mediator.Send(new DeleteConversationCommand(HttpContext.GetUserId(), conversationId), cancellationToken);
        return result.ToPayGateAwareResult(() => NoContent());
    }
}
