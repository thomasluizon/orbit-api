using System.Security.Cryptography;
using System.Text;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
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
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork,
    IMemoryCache cache,
    ILogger<SuggestHabitSetupCommandHandler> logger)
    : IRequestHandler<SuggestHabitSetupCommand, Result<HabitSetupSuggestion>>
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    public async Task<Result<HabitSetupSuggestion>> Handle(
        SuggestHabitSetupCommand request, CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanSendAiMessage(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck.PropagateError<HabitSetupSuggestion>();

        var language = string.IsNullOrWhiteSpace(request.Language) ? "en" : request.Language;
        var cacheKey = BuildCacheKey(request.UserId, request.Title, language);

        if (cache.TryGetValue(cacheKey, out HabitSetupSuggestion? cached) && cached is not null)
            return Result.Success(cached);

        var suggestionResult = await suggestionService.SuggestSetupAsync(
            request.Title, language, cancellationToken);
        if (suggestionResult.IsFailure)
            return suggestionResult;

        await IncrementUsageAsync(request.UserId, cancellationToken);

        cache.Set(cacheKey, suggestionResult.Value, CacheTtl);

        return suggestionResult;
    }

    private async Task IncrementUsageAsync(Guid userId, CancellationToken cancellationToken)
    {
        var increment = await ConcurrencyRetry.ExecuteAsync(
            userRepository,
            unitOfWork,
            ct => userRepository.FindOneTrackedAsync(user => user.Id == userId, cancellationToken: ct),
            user =>
            {
                user.IncrementAiMessageCount();
                return Task.FromResult(Result.Success());
            },
            ErrorMessages.UserNotFound,
            cancellationToken);

        if (increment.IsFailure)
            LogUsageIncrementFailed(logger, userId);
    }

    private static string BuildCacheKey(Guid userId, string title, string language)
    {
        var normalizedTitle = title.Trim().ToLowerInvariant();
        var titleHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedTitle)));
        return $"suggest-setup:{userId}:{titleHash}:{language.ToLowerInvariant()}";
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to increment AI message usage after a habit suggestion for user {UserId}")]
    private static partial void LogUsageIncrementFailed(ILogger logger, Guid userId);
}
