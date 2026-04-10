using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Calendar.Commands;

public record DismissCalendarSuggestionCommand(Guid UserId, Guid SuggestionId) : IRequest<Result>;

public class DismissCalendarSuggestionCommandHandler(
    IGenericRepository<GoogleCalendarSyncSuggestion> suggestionRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<DismissCalendarSuggestionCommand, Result>
{
    public async Task<Result> Handle(DismissCalendarSuggestionCommand request, CancellationToken cancellationToken)
    {
        var suggestion = await suggestionRepository.FindOneTrackedAsync(
            s => s.Id == request.SuggestionId && s.UserId == request.UserId,
            cancellationToken: cancellationToken);

        if (suggestion is null)
            return Result.Failure("Suggestion not found.");

        suggestion.MarkDismissed(DateTime.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
