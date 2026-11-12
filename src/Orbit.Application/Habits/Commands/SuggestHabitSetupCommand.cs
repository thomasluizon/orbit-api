using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Habits.Commands;

public record SuggestHabitSetupCommand(
    Guid UserId,
    string Title,
    string Language) : IRequest<Result<HabitSetupSuggestion>>;

public partial class SuggestHabitSetupCommandHandler(
    IPayGateService payGate,
    IHabitSuggestionService suggestionService,
    IUnitOfWork unitOfWork,
    IMemoryCache cache)
    : IRequestHandler<SuggestHabitSetupCommand, Result<HabitSetupSuggestion>>
{
    public async Task<Result<HabitSetupSuggestion>> Handle(
        SuggestHabitSetupCommand request, CancellationToken cancellationToken)
    {
        var language = string.IsNullOrWhiteSpace(request.Language) ? "en" : request.Language;

        if (HabitSetupSuggestionCache.TryGet(cache, request.UserId, request.Title, language, out var cached))
            return Result.Success(cached);

        var reservation = await payGate.TryConsumeAiMessage(
            request.UserId,
            unitOfWork,
            cancellationToken);
        if (reservation.IsFailure)
            return reservation.PropagateError<HabitSetupSuggestion>();

        var suggestionResult = await suggestionService.SuggestSetupAsync(
            request.Title, language, cancellationToken);
        if (suggestionResult.IsFailure)
            return suggestionResult;

        HabitSetupSuggestionCache.Set(cache, request.UserId, request.Title, language, suggestionResult.Value);

        return suggestionResult;
    }
}
