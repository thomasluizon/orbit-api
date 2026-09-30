using System.Linq.Expressions;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Application.ApiKeys.Commands;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Calendar.Commands;
using Orbit.Application.ChecklistTemplates.Commands;
using Orbit.Application.Common;
using Orbit.Application.Goals.Commands;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Notifications.Commands;
using Orbit.Application.Profile.Commands;
using Orbit.Application.Subscriptions.Commands;
using Orbit.Application.Support.Commands;
using Orbit.Application.Tags.Commands;
using Orbit.Application.UserFacts.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools;

/// <summary>
/// Checks the command produced by a chat tool's ordinary parser without dispatching that command.
/// Command validators remain the source of parameter limits; entity reads check ownership and
/// domain guards before an edited approval preview can be stored.
/// </summary>
public sealed record CheckChatCommandQuery(object Command) : IRequest<Result>;

public sealed class CheckChatCommandQueryHandler(IServiceProvider services) : IRequestHandler<CheckChatCommandQuery, Result>
{
    public async Task<Result> Handle(CheckChatCommandQuery request, CancellationToken ct)
    {
        var command = request.Command;
        var validatorType = typeof(IValidator<>).MakeGenericType(command.GetType());
        foreach (var validator in services.GetServices(validatorType).OfType<IValidator>())
        {
            var validation = await validator.ValidateAsync(new ValidationContext<object>(command), ct);
            if (!validation.IsValid)
                return Result.Failure(validation.ToString());
        }

        return await CheckHabitAsync(command, ct)
            ?? await CheckGoalAsync(command, ct)
            ?? await CheckTagAsync(command, ct)
            ?? await CheckProfileAsync(command, ct)
            ?? await CheckNotificationAsync(command, ct)
            ?? await CheckOtherAsync(command, ct)
            ?? Result.Failure("Unsupported argument check.");
    }

    private IGenericRepository<T> Repository<T>() where T : Entity =>
        services.GetRequiredService<IGenericRepository<T>>();

    private async Task<Result> ExistsAsync<T>(Expression<Func<T, bool>> predicate, CancellationToken ct) where T : Entity =>
        await Repository<T>().AnyAsync(predicate, ct) ? Result.Success() : Result.Failure("Referenced record not found.");

    private async Task<Result?> CheckHabitAsync(object command, CancellationToken ct) => command switch
    {
        CreateSubHabitCommand create => await CheckSubHabitAsync(create, ct),
        UpdateChecklistCommand update => await ExistsAsync<Habit>(h => h.Id == update.HabitId && h.UserId == update.UserId, ct),
        DeleteHabitCommand delete => await ExistsAsync<Habit>(h => h.Id == delete.HabitId && h.UserId == delete.UserId, ct),
        DuplicateHabitCommand duplicate => await ExistsAsync<Habit>(h => h.Id == duplicate.HabitId && h.UserId == duplicate.UserId, ct),
        MoveHabitParentCommand move => await MoveHabitParentCommandHandler.CheckArgumentsAsync(move,
            Repository<Habit>(), services.GetRequiredService<IAppConfigService>(), ct),
        LinkGoalsToHabitCommand link => await CheckGoalLinksAsync(link, ct),
        ReorderHabitsCommand reorder => await CheckHabitIdsAsync(reorder.UserId, reorder.Positions.Select(p => p.HabitId).ToList(), ct),
        LogHabitCommand log => await CheckLogAsync(log, ct),
        BulkCreateHabitsCommand create => await CheckBulkCreateAsync(create, ct),
        BulkUpdateHabitsCommand update => await CheckBulkUpdateAsync(update, ct),
        BulkLogHabitsCommand log => await CheckBulkLogsAsync(log, ct),
        BulkSkipHabitsCommand skip => await CheckBulkSkipsAsync(skip, ct),
        BulkDeleteHabitsCommand delete => await CheckHabitIdsAsync(delete.UserId, delete.HabitIds, ct),
        _ => null
    };

    private async Task<Result> CheckHabitIdsAsync(Guid userId, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var habits = await Repository<Habit>().FindAsync(h => h.UserId == userId && ids.Contains(h.Id), ct);
        return OwnershipValidation.AllResolved(ids, habits, h => h.Id, ErrorMessages.HabitNotFound);
    }

    private async Task<Result> CheckGoalLinksAsync(LinkGoalsToHabitCommand command, CancellationToken ct)
    {
        var habit = await ExistsAsync<Habit>(h => h.Id == command.HabitId && h.UserId == command.UserId, ct);
        if (habit.IsFailure)
            return habit;
        var goals = await Repository<Goal>().FindAsync(g => g.UserId == command.UserId && command.GoalIds.Contains(g.Id), ct);
        return OwnershipValidation.AllResolved(command.GoalIds, goals, g => g.Id, ErrorMessages.GoalNotFound);
    }

    private async Task<Result?> CheckGoalAsync(object command, CancellationToken ct) => command switch
    {
        LinkHabitsToGoalCommand link => (await ExistsAsync<Goal>(g => g.Id == link.GoalId && g.UserId == link.UserId, ct)) is { IsFailure: true } failure
            ? failure : await CheckHabitIdsAsync(link.UserId, link.HabitIds, ct),
        ReorderGoalsCommand reorder => await CheckGoalPositionsAsync(reorder, ct),
        _ => null
    };

    private async Task<Result> CheckGoalPositionsAsync(ReorderGoalsCommand command, CancellationToken ct)
    {
        if (command.Positions.Any(p => p.Position < 0)
            || command.Positions.Select(p => p.GoalId).Distinct().Count() != command.Positions.Count)
            return Result.Failure("Invalid goal positions.");
        var ids = command.Positions.Select(p => p.GoalId).ToList();
        var goals = await Repository<Goal>().FindAsync(g => g.UserId == command.UserId && ids.Contains(g.Id), ct);
        return OwnershipValidation.AllResolved(ids, goals, g => g.Id, ErrorMessages.GoalNotFound);
    }

    private async Task<Result?> CheckTagAsync(object command, CancellationToken ct) => command switch
    {
        CreateTagCommand create => await CheckTagNameAsync(create.UserId, null, create.Name, create.Color, ct),
        UpdateTagCommand update => (await ExistsAsync<Tag>(t => t.Id == update.TagId && t.UserId == update.UserId, ct)) is { IsFailure: true } failure
            ? failure : await CheckTagNameAsync(update.UserId, update.TagId, update.Name, update.Color, ct),
        DeleteTagCommand delete => await ExistsAsync<Tag>(t => t.Id == delete.TagId && t.UserId == delete.UserId, ct),
        CreateChecklistTemplateCommand create => ChecklistTemplate.Create(create.UserId, create.Name, create.Items),
        DeleteChecklistTemplateCommand delete => await ExistsAsync<ChecklistTemplate>(t => t.Id == delete.TemplateId && t.UserId == delete.UserId, ct),
        _ => null
    };

    private async Task<Result> CheckTagNameAsync(Guid userId, Guid? tagId, string name, string color, CancellationToken ct)
    {
        if (await Repository<Tag>().AnyAsync(t => t.UserId == userId && t.Name == name.Trim() && t.Id != tagId, ct))
            return Result.Failure(ErrorMessages.DuplicateTagName);
        return Tag.Create(userId, name, color);
    }

    private async Task<Result?> CheckProfileAsync(object command, CancellationToken ct) => command switch
    {
        SetTimezoneCommand timezone => CheckTimezone(timezone.TimeZone),
        SetLanguageCommand => Result.Success(),
        SetWeekStartDayCommand => Result.Success(),
        SetClockFormatCommand => Result.Success(),
        SetThemePreferenceCommand theme => theme.Preference is null or "light" or "dark"
            ? Result.Success() : Result.Failure("Invalid theme preference."),
        SetAiMemoryCommand or SetAiSummaryCommand or CompleteOnboardingCommand or CompleteTourCommand or ResetTourCommand => Result.Success(),
        _ => await Task.FromResult<Result?>(null)
    };

    private static Result CheckTimezone(string timezone)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(timezone);
            return Result.Success();
        }
        catch (TimeZoneNotFoundException)
        {
            return Result.Failure("Invalid timezone.");
        }
        catch (InvalidTimeZoneException)
        {
            return Result.Failure("Invalid timezone.");
        }
    }

    private async Task<Result?> CheckNotificationAsync(object command, CancellationToken ct) => command switch
    {
        MarkNotificationReadCommand mark => await ExistsAsync<Notification>(n => n.Id == mark.NotificationId && n.UserId == mark.UserId, ct),
        DeleteNotificationCommand delete => await ExistsAsync<Notification>(n => n.Id == delete.NotificationId && n.UserId == delete.UserId, ct),
        SubscribePushCommand subscribe => await CheckPushSubscriptionAsync(subscribe, ct),
        UnsubscribePushCommand => Result.Success(),
        MarkAllNotificationsReadCommand or DeleteAllNotificationsCommand or TestPushNotificationCommand => Result.Success(),
        _ => null
    };

    private async Task<Result> CheckPushSubscriptionAsync(SubscribePushCommand command, CancellationToken ct)
    {
        if (PushSubscription.ClassifyTransport(command.P256dh) != PushTransport.Fcm
            && (!Uri.TryCreate(command.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
            return Result.Failure(ErrorMessages.PushEndpointInvalid);
        var subscriptions = await Repository<PushSubscription>().FindAsync(s => s.Endpoint == command.Endpoint, ct);
        var existing = subscriptions.FirstOrDefault();
        if (existing is not null && existing.UserId != command.UserId && !existing.MatchesCredentials(command.P256dh, command.Auth))
            return Result.Failure(ErrorMessages.PushEndpointOwnedByOtherUser);
        return PushSubscription.Create(command.UserId, command.Endpoint, command.P256dh, command.Auth);
    }

    private async Task<Result?> CheckOtherAsync(object command, CancellationToken ct) => command switch
    {
        CreateApiKeyCommand create => ApiKey.Create(create.UserId, create.Name, create.Scopes, create.IsReadOnly, create.ExpiresAtUtc),
        RevokeApiKeyCommand revoke => await ExistsAsync<ApiKey>(k => k.Id == revoke.KeyId && k.UserId == revoke.UserId, ct),
        DeleteUserFactCommand delete => await ExistsAsync<UserFact>(f => f.Id == delete.FactId && f.UserId == delete.UserId, ct),
        BulkDeleteUserFactsCommand delete => await CheckFactIdsAsync(delete, ct),
        DismissCalendarSuggestionCommand dismiss => await ExistsAsync<GoogleCalendarSyncSuggestion>(s => s.Id == dismiss.SuggestionId && s.UserId == dismiss.UserId, ct),
        ConfirmAccountDeletionCommand confirm => await CheckDeletionCodeAsync(confirm, ct),
        CreateCheckoutCommand checkout => checkout.Interval is "monthly" or "yearly" ? Result.Success() : Result.Failure("Invalid interval."),
        CreatePortalSessionCommand or SendSupportCommand or ResetAccountCommand or RequestAccountDeletionCommand
            or SetCalendarAutoSyncCommand or RunCalendarAutoSyncCommand or DismissCalendarImportCommand => Result.Success(),
        _ => null
    };

    private async Task<Result> CheckFactIdsAsync(BulkDeleteUserFactsCommand command, CancellationToken ct)
    {
        var facts = await Repository<UserFact>().FindAsync(f => f.UserId == command.UserId && command.FactIds.Contains(f.Id), ct);
        return OwnershipValidation.AllResolved(command.FactIds, facts, f => f.Id, ErrorMessages.FactNotFound);
    }

    private async Task<Result> CheckDeletionCodeAsync(ConfirmAccountDeletionCommand command, CancellationToken ct)
    {
        var user = await Repository<User>().GetByIdAsync(command.UserId, ct);
        return user is null ? Result.Failure(ErrorMessages.UserNotFound)
            : services.GetRequiredService<Orbit.Application.Auth.Services.EmailChallengeService>()
                .CheckConfirmation(Orbit.Application.Auth.Services.EmailChallengeOperation.AccountDeletion, user.Email, command.Code);
    }

    private Task<Result> CheckSubHabitAsync(CreateSubHabitCommand command, CancellationToken ct) =>
        CreateSubHabitCommandHandler.CheckArgumentsAsync(command, Repository<Habit>(),
            services.GetRequiredService<IUserDateService>(), services.GetRequiredService<IAppConfigService>(), ct);

    private Task<Result> CheckLogAsync(LogHabitCommand command, CancellationToken ct) =>
        LogHabitCommandHandler.CheckArgumentsAsync(command, Repository<Habit>(), Repository<HabitLog>(),
            services.GetRequiredService<IUserDateService>(), ct);

    private Task<Result> CheckBulkCreateAsync(BulkCreateHabitsCommand command, CancellationToken ct) =>
        BulkCreateHabitsCommandHandler.CheckArgumentsAsync(command, services.GetRequiredService<IUserDateService>(), ct);

    private Task<Result> CheckBulkUpdateAsync(BulkUpdateHabitsCommand command, CancellationToken ct) =>
        BulkUpdateHabitsCommandHandler.CheckArgumentsAsync(command, Repository<Habit>(), services.GetRequiredService<IUserDateService>(), ct);

    private async Task<Result> CheckBulkLogsAsync(BulkLogHabitsCommand command, CancellationToken ct)
    {
        foreach (var item in command.Items)
        {
            var result = await CheckLogAsync(new LogHabitCommand(command.UserId, item.HabitId, item.Date), ct);
            if (result.IsFailure)
                return result;
        }
        return Result.Success();
    }

    private Task<Result> CheckBulkSkipsAsync(BulkSkipHabitsCommand command, CancellationToken ct) =>
        BulkSkipHabitsCommandHandler.CheckArgumentsAsync(command, Repository<Habit>(), Repository<HabitLog>(),
            services.GetRequiredService<IUserDateService>(), ct);
}
