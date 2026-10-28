using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Application.Habits.Commands;

namespace Orbit.Api.Controllers;

public partial class HabitsController
{
    public sealed record CorrectDoseRequest(DateTime TakenAtUtc);

    [HttpPatch("{id:guid}/logs/{logId:guid}/dose")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CorrectDose(Guid id, Guid logId, CorrectDoseRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new CorrectHabitDoseCommand(
            HttpContext.GetUserId(), id, logId, request.TakenAtUtc), cancellationToken);
        return result.ToPayGateAwareResult(() => NoContent());
    }
}
