using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

using Orbit.Application.Common.Attributes;

namespace Orbit.Application.Habits.Commands;

[AiAction(
    "DeleteHabit",
    """**Delete habits** when asked (e.g., "delete my running habit", "remove all bad habits")""",
    """
    - User explicitly asks to delete, remove, or get rid of a habit: "delete my running habit", "remove meditation"
    - User says "I don't want to track X anymore"
    - For a SINGLE habit deletion, execute immediately
    - For bulk deletes (2+ habits, e.g. "remove all my bad habits"), do NOT execute immediately. List the habits that would be deleted in aiMessage and ask for confirmation first. Only delete after they confirm.
    - ALWAYS confirm in aiMessage what was deleted
    """,
    DisplayOrder = 40)]
[AiExample(
    "Delete my running habit",
    """{ "actions": [{ "type": "DeleteHabit", "habitId": "abc-123" }], "aiMessage": "Deleted Running!" }""",
    Note = """Running ID: "abc-123" """)]
public record DeleteHabitCommand(
    Guid UserId,
    [property: AiField("string", "ID of existing habit to delete", Required = true)] Guid HabitId) : IRequest<Result>;

public class DeleteHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IUnitOfWork unitOfWork,
    IMemoryCache cache) : IRequestHandler<DeleteHabitCommand, Result>
{
    public async Task<Result> Handle(DeleteHabitCommand request, CancellationToken cancellationToken)
    {
        var habit = await habitRepository.GetByIdAsync(request.HabitId, cancellationToken);

        if (habit is null)
            return Result.Failure(ErrorMessages.HabitNotFound);

        if (habit.UserId != request.UserId)
            return Result.Failure(ErrorMessages.NoPermission);

        habitRepository.Remove(habit);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        CacheInvalidationHelper.InvalidateSummaryCache(cache, request.UserId);

        return Result.Success();
    }
}
