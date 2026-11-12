using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Habits.Commands;

public record ReadHabitPhraseCommand(
    Guid UserId,
    string Phrase,
    string Language) : IRequest<Result<HabitPhraseReading>>;

public partial class ReadHabitPhraseCommandHandler(
    IHabitSuggestionService suggestionService,
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork,
    IUserDateService userDateService,
    IAppConfigService appConfig,
    IFeatureFlagService featureFlags,
    IProductAnalytics analytics,
    IMemoryCache cache,
    ILogger<ReadHabitPhraseCommandHandler> logger)
    : IRequestHandler<ReadHabitPhraseCommand, Result<HabitPhraseReading>>
{
    private const string EventName = "phrase_read";
    private static readonly AppError CapReached = new("PHRASE_READ_CAP_REACHED", "The daily phrase read cap is reached.");

    public async Task<Result<HabitPhraseReading>> Handle(
        ReadHabitPhraseCommand request, CancellationToken cancellationToken)
    {
        var language = string.IsNullOrWhiteSpace(request.Language) ? "en" : request.Language;

        if (HabitSetupSuggestionCache.TryGet(cache, request.UserId, request.Phrase, language, out var cached))
            return Finish(request.UserId, "cache", HabitPhraseReading.From(cached));

        var enabledFlags = await featureFlags.GetEnabledKeysForUserAsync(request.UserId, cancellationToken);
        if (enabledFlags.Contains(FeatureFlagKeys.PhraseReaderAiDisabled, StringComparer.Ordinal))
            return Finish(request.UserId, "disabled", HabitPhraseReading.Unread);

        var reservation = await ReserveReadAsync(request.UserId, cancellationToken);
        if (reservation.IsFailure)
        {
            return reservation.ErrorCode == CapReached.Code
                ? Finish(request.UserId, "cap", HabitPhraseReading.Unread)
                : reservation.PropagateError<HabitPhraseReading>();
        }

        var suggestion = await suggestionService.SuggestSetupAsync(request.Phrase, language, cancellationToken);
        if (suggestion.IsFailure)
        {
            LogReadFailed(logger, request.UserId, suggestion.ErrorCode);
            return Finish(request.UserId, "failed", HabitPhraseReading.Unread);
        }

        HabitSetupSuggestionCache.Set(cache, request.UserId, request.Phrase, language, suggestion.Value);
        return Finish(request.UserId, "ai", HabitPhraseReading.From(suggestion.Value));
    }

    private async Task<Result> ReserveReadAsync(Guid userId, CancellationToken cancellationToken)
    {
        var cap = await appConfig.GetAsync(
            AppConfigKeys.PhraseReadsPerDay, AppConstants.DefaultPhraseReadsPerDay, cancellationToken);
        var userToday = await userDateService.GetUserTodayAsync(userId, cancellationToken);

        var reserved = await ConcurrencyRetry.ExecuteAsync(
            userRepository,
            unitOfWork,
            token => userRepository.FindOneTrackedAsync(user => user.Id == userId, cancellationToken: token),
            user => Task.FromResult(user.TryReservePhraseRead(userToday, cap)
                ? Result.Success()
                : Result.Failure(CapReached)),
            ErrorMessages.UserNotFound,
            cancellationToken);

        return reserved.IsSuccess ? Result.Success() : reserved.PropagateError();
    }

    private Result<HabitPhraseReading> Finish(Guid userId, string outcome, HabitPhraseReading reading)
    {
        if (logger.IsEnabled(LogLevel.Debug))
            LogPhraseRead(logger, userId, outcome);

        AnalyticsCapture.SafeCaptureUserEvent(analytics, logger, userId, null, EventName,
            new Dictionary<string, object> { ["outcome"] = outcome });

        return Result.Success(reading);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Phrase read for user {UserId} ended with outcome {Outcome}")]
    private static partial void LogPhraseRead(ILogger logger, Guid userId, string outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Phrase read for user {UserId} returned no result: {ErrorCode}")]
    private static partial void LogReadFailed(ILogger logger, Guid userId, string? errorCode);
}
