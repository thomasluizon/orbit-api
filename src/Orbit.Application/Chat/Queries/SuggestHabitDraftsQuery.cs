using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Models;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Queries;

public sealed record SuggestHabitDraftsQuery(Guid UserId, string Instruction, string Language = "en", int Count = 3)
    : IRequest<Result<HabitSuggestionsCard>>;

public sealed class SuggestHabitDraftsQueryHandler(HabitSuggestionService suggestions)
    : IRequestHandler<SuggestHabitDraftsQuery, Result<HabitSuggestionsCard>>
{
    public Task<Result<HabitSuggestionsCard>> Handle(SuggestHabitDraftsQuery request, CancellationToken ct) =>
        suggestions.GenerateAsync(request.UserId, request.Instruction, request.Language, request.Count, null, ct);
}

public sealed record RefineHabitSuggestionRequest(HabitSuggestionDraft Draft, string Instruction, string Language = "en");

public sealed record RefineHabitSuggestionQuery(Guid UserId, HabitSuggestionDraft Draft, string Instruction, string Language = "en")
    : IRequest<Result<HabitSuggestionDraft>>;

public sealed class RefineHabitSuggestionQueryHandler(HabitSuggestionService suggestions, IPayGateService payGate,
    IUnitOfWork unitOfWork, IContentModerationService moderation)
    : IRequestHandler<RefineHabitSuggestionQuery, Result<HabitSuggestionDraft>>
{
    public async Task<Result<HabitSuggestionDraft>> Handle(RefineHabitSuggestionQuery request, CancellationToken ct)
    {
        var check = await moderation.CheckTextAsync(request.Instruction, ct);
        if (check.Unavailable) return Result.Failure<HabitSuggestionDraft>(ErrorMessages.AiUnavailable);
        if (check.Flagged) return Result.Failure<HabitSuggestionDraft>(ErrorMessages.ContentModerationRefused);
        var reservation = await payGate.TryConsumeAiMessage(request.UserId, unitOfWork, ct);
        if (reservation.IsFailure) return reservation.PropagateError<HabitSuggestionDraft>();
        var result = await suggestions.GenerateAsync(request.UserId, request.Instruction, request.Language, 1, request.Draft, ct);
        return result.IsSuccess ? Result.Success(result.Value.Drafts[0]) : result.PropagateError<HabitSuggestionDraft>();
    }
}
