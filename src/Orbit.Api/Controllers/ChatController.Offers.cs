using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Application.Chat.Commands;

namespace Orbit.Api.Controllers;

public sealed record DismissConversationOffersRequest(Guid ConversationId, IReadOnlyList<string> OfferKeys);

public partial class ChatController
{
    [HttpPost("offers/dismiss")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DismissOffers([FromBody] DismissConversationOffersRequest request, CancellationToken ct)
    {
        if (!CanAccessChat()) return Forbid();
        var result = await mediator.Send(new DismissConversationOffersCommand(HttpContext.GetUserId(),
            request.ConversationId, request.OfferKeys), ct);
        return result.ToPayGateAwareResult(() => NoContent());
    }
}
