using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

using Orbit.Application.Common.Attributes;

namespace Orbit.Application.Tags.Commands;

[AiAction(
    "AssignTags",
    """**Manage tags** on habits (assign, remove, create new tags when the user asks)""",
    """
    - User says "tag my running habit as health" -> AssignTags with habitId and tagNames: ["health"]
    - User says "add health and fitness tags to my gym habit" -> AssignTags with tagNames: ["health", "fitness"]
    - User says "remove all tags from my reading habit" -> AssignTags with tagNames: []
    - User says "create a daily run habit tagged as health" -> CreateHabit with tagNames: ["health"]
    - NEVER assign tags unless the user explicitly asks. Creating or logging habits without tag mention = no tagNames field
    """,
    DisplayOrder = 60)]
[AiRule("AssignTags action requires habitId and tagNames array. An empty tagNames array removes all tags from the habit")]
[AiExample(
    "Tag my running habit as health",
    """{ "actions": [{ "type": "AssignTags", "habitId": "abc-123", "tagNames": ["health"] }], "aiMessage": "Tagged Running as health!" }""",
    Note = """Running ID: "abc-123" """)]
public record AssignTagsCommand(
    Guid UserId,
    [property: AiField("string", "ID of existing habit", Required = true)] Guid HabitId,
    [property: AiField("string[]", "Array of tag name strings. Empty array [] removes all tags", Required = true, Name = "tagNames")] IReadOnlyList<Guid> TagIds) : IRequest<Result>;

public class AssignTagsCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<Tag> tagRepository,
    IAppConfigService appConfigService,
    IUnitOfWork unitOfWork) : IRequestHandler<AssignTagsCommand, Result>
{
    public async Task<Result> Handle(AssignTagsCommand request, CancellationToken cancellationToken)
    {
        var maxTags = await appConfigService.GetAsync("MaxTagsPerHabit", AppConstants.MaxTagsPerHabit, cancellationToken);

        if (request.TagIds.Count > maxTags)
            return Result.Failure($"A habit can have at most {maxTags} tags.");

        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.HabitId && h.UserId == request.UserId,
            q => q.Include(h => h.Tags),
            cancellationToken);

        if (habit is null)
            return Result.Failure(ErrorMessages.HabitNotFound);

        // Load requested tags
        var tags = await tagRepository.FindAsync(
            t => request.TagIds.Contains(t.Id) && t.UserId == request.UserId,
            cancellationToken);

        // Clear existing and set new
        foreach (var existing in habit.Tags.ToList())
            habit.RemoveTag(existing);

        foreach (var tag in tags)
            habit.AddTag(tag);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
