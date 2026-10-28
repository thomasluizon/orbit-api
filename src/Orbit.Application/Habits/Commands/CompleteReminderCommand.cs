using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Habits.Commands;

public sealed record CompleteReminderCommand(string Token, Guid? RequestingUserId = null) : IRequest<Result<LogHabitResponse>>;

public sealed class CompleteReminderCommandHandler(
    IReminderActionTokenService tokens, IGenericRepository<Habit> habits, IGenericRepository<SentReminder> reminders,
    IUnitOfWork unitOfWork, IUserDateService dates, IMediator mediator, TimeProvider time)
    : IRequestHandler<CompleteReminderCommand, Result<LogHabitResponse>>
{
    private static readonly AppError InvalidAction = new("INVALID_REMINDER_ACTION", "This reminder action is no longer available.");

    public Task<Result<LogHabitResponse>> Handle(CompleteReminderCommand request, CancellationToken cancellationToken)
    {
        if (!tokens.TryRead(request.Token, out var claims) || request.RequestingUserId is { } caller && caller != claims.UserId)
            return Task.FromResult(Result.Failure<LogHabitResponse>(InvalidAction));
        return HabitCeilingLock.ExecuteAsync(unitOfWork, claims.UserId,
            ct => Complete(claims, ct), cancellationToken);
    }

    private async Task<Result<LogHabitResponse>> Complete(ReminderActionClaims claims, CancellationToken ct)
    {
        var nowAtUtc = time.GetUtcNow().UtcDateTime;
        if (nowAtUtc < claims.ReminderAtUtc || nowAtUtc >= claims.ExpiresAtUtc)
            return Result.Failure<LogHabitResponse>(InvalidAction);
        var habit = await habits.FindOneTrackedAsync(h => h.Id == claims.HabitId && h.UserId == claims.UserId,
            cancellationToken: ct);
        var today = await dates.GetUserTodayAsync(claims.UserId, ct);
        if (habit is null || habit.IsDeleted || habit.IsCompleted || habit.EndDate is { } end && today > end
            || !habit.ReminderEnabled || habit.RepeatIntervalMinutes is null)
            return Result.Failure<LogHabitResponse>(InvalidAction);
        var reminder = await reminders.FindOneTrackedAsync(r => r.Id == claims.ReminderId
            && r.HabitId == claims.HabitId && r.ReminderAtUtc == claims.ReminderAtUtc
            && r.ExpiresAtUtc == claims.ExpiresAtUtc && r.ParentReminderId == null, cancellationToken: ct);
        if (reminder is null || reminder.ConsumedAtUtc is not null)
            return Result.Failure<LogHabitResponse>(InvalidAction);
        var result = await mediator.Send(new LogHabitCommand(claims.UserId, claims.HabitId,
            ToggleExisting: false, TakenAtUtc: nowAtUtc, ReminderAtUtc: claims.ReminderAtUtc), ct);
        if (result.IsFailure)
            return result;
        if (!reminder.TryConsume(nowAtUtc))
            throw new InvalidOperationException("The locked reminder action could not be consumed.");
        await unitOfWork.SaveChangesAsync(ct);
        return result;
    }
}
