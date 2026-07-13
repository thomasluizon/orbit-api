using System.Security.Cryptography;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Social.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Challenges.Commands;

public record CreateChallengeCommand(
    Guid UserId,
    ChallengeType Type,
    string Title,
    string? Description,
    int? TargetCount,
    DateOnly PeriodStartUtc,
    DateOnly? PeriodEndUtc,
    IReadOnlyList<Guid> LinkedHabitIds,
    IReadOnlyList<Guid> InvitedFriendUserIds) : IRequest<Result<Guid>>;

public partial class CreateChallengeCommandHandler(
    SocialAccessGuard socialAccessGuard,
    FriendGraphService friendGraphService,
    IGenericRepository<Challenge> challengeRepository,
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<User> userRepository,
    IServiceScopeFactory scopeFactory,
    IUnitOfWork unitOfWork,
    ILogger<CreateChallengeCommandHandler> logger) : IRequestHandler<CreateChallengeCommand, Result<Guid>>
{
    private const string JoinCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int JoinCodeLength = 8;
    private const int MaxInvitePushConcurrency = 5;

    public async Task<Result<Guid>> Handle(CreateChallengeCommand request, CancellationToken cancellationToken)
    {
        var access = await socialAccessGuard.EnsureEnabledAsync(request.UserId, cancellationToken);
        if (access.IsFailure)
            return access.PropagateError<Guid>();
        var requester = access.Value;

        var ownedHabits = await VerifyOwnedHabitsAsync(request.UserId, request.LinkedHabitIds, cancellationToken);
        if (ownedHabits.IsFailure)
            return ownedHabits.PropagateError<Guid>();

        var invitedFriendIds = request.InvitedFriendUserIds
            .Where(id => id != request.UserId)
            .Distinct()
            .ToList();

        if (1 + invitedFriendIds.Count > AppConstants.MaxChallengeParticipants)
            return Result.Failure<Guid>(ErrorMessages.ChallengeFull.Format(AppConstants.MaxChallengeParticipants));

        var friendCheck = await VerifyInvitedFriendsAsync(request.UserId, invitedFriendIds, cancellationToken);
        if (friendCheck.IsFailure)
            return friendCheck.PropagateError<Guid>();

        var joinCode = await GenerateUniqueJoinCodeAsync(cancellationToken);

        var createResult = Challenge.Create(new CreateChallengeParams(
            request.UserId,
            request.Type,
            request.Title,
            request.Description,
            request.TargetCount,
            request.PeriodStartUtc,
            request.PeriodEndUtc,
            joinCode));
        if (createResult.IsFailure)
            return createResult.PropagateError<Guid>();

        var challenge = createResult.Value;
        challenge.AddParticipant(request.UserId, request.LinkedHabitIds);
        foreach (var friendId in invitedFriendIds)
            challenge.AddParticipant(friendId, []);

        await challengeRepository.AddAsync(challenge, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        await NotifyInvitedFriendsAsync(requester, invitedFriendIds, cancellationToken);

        return Result.Success(challenge.Id);
    }

    private async Task NotifyInvitedFriendsAsync(User requester, List<Guid> invitedFriendIds, CancellationToken cancellationToken)
    {
        if (invitedFriendIds.Count == 0)
            return;

        var invitedUsers = await userRepository.FindAsync(u => invitedFriendIds.Contains(u.Id), cancellationToken);

        using var throttle = new SemaphoreSlim(MaxInvitePushConcurrency);
        await Task.WhenAll(invitedUsers.Select(user => NotifyOneAsync(requester, user, throttle, cancellationToken)));
    }

    private async Task NotifyOneAsync(User requester, User user, SemaphoreSlim throttle, CancellationToken cancellationToken)
    {
        await throttle.WaitAsync(cancellationToken);
        try
        {
            var isPortuguese = LocaleHelper.IsPortuguese(user.Language);
            var title = isPortuguese ? "Novo desafio em grupo" : "New group challenge";
            var body = isPortuguese
                ? $"{requester.Name} convidou você para um desafio."
                : $"{requester.Name} invited you to a challenge.";

            using var scope = scopeFactory.CreateScope();
            var pushNotificationService = scope.ServiceProvider.GetRequiredService<IPushNotificationService>();
            await pushNotificationService.SendToUserAsync(user.Id, title, body, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            LogPushNotificationFailed(logger, ex, user.Id);
        }
        finally
        {
            throttle.Release();
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Challenge invite push failed for user {UserId}")]
    private static partial void LogPushNotificationFailed(ILogger logger, Exception ex, Guid userId);

    private async Task<Result> VerifyOwnedHabitsAsync(Guid userId, IReadOnlyList<Guid> habitIds, CancellationToken cancellationToken)
    {
        var distinctIds = habitIds.Distinct().ToList();
        var owned = await habitRepository.CountAsync(
            h => h.UserId == userId && distinctIds.Contains(h.Id),
            cancellationToken);

        return owned == distinctIds.Count
            ? Result.Success()
            : Result.Failure(ErrorMessages.HabitNotFound);
    }

    private async Task<Result> VerifyInvitedFriendsAsync(Guid userId, IReadOnlyList<Guid> invitedFriendIds, CancellationToken cancellationToken)
    {
        foreach (var friendId in invitedFriendIds)
        {
            if (!await friendGraphService.AreAcceptedFriendsAsync(userId, friendId, cancellationToken))
                return Result.Failure(ErrorMessages.NotFriends);

            if (await friendGraphService.IsBlockedBetweenAsync(userId, friendId, cancellationToken))
                return Result.Failure(ErrorMessages.NotFriends);
        }

        return Result.Success();
    }

    private async Task<string> GenerateUniqueJoinCodeAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var code = GenerateJoinCode();
            var exists = await challengeRepository.AnyAsync(c => c.JoinCode == code, cancellationToken);
            if (!exists)
                return code;
        }
    }

    private static string GenerateJoinCode()
    {
        return string.Create(JoinCodeLength, JoinCodeAlphabet, static (span, alphabet) =>
        {
            Span<byte> bytes = stackalloc byte[span.Length];
            RandomNumberGenerator.Fill(bytes);
            for (var i = 0; i < span.Length; i++)
                span[i] = alphabet[bytes[i] % alphabet.Length];
        });
    }
}
