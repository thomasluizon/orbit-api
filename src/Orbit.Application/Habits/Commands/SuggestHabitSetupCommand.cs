using System.Security.Cryptography;
using System.Text;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
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
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    public async Task<Result<HabitSetupSuggestion>> Handle(
        SuggestHabitSetupCommand request, CancellationToken cancellationToken)
    {
        var language = string.IsNullOrWhiteSpace(request.Language) ? "en" : request.Language;
        var cacheKey = BuildCacheKey(request.UserId, request.Title, language);

        if (cache.TryGetValue(cacheKey, out HabitSetupSuggestion? cached) && cached is not null)
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

        cache.Set(cacheKey, suggestionResult.Value, CacheTtl);

        return suggestionResult;
    }

    private static string BuildCacheKey(Guid userId, string title, string language)
    {
        var normalizedTitle = title.Trim().ToLowerInvariant();
        var titleHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedTitle)));
        return $"suggest-setup:{userId}:{titleHash}:{language.ToLowerInvariant()}";
    }
}
