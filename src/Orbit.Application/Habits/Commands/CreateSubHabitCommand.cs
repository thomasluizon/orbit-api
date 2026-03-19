using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public record CreateSubHabitCommand(
    Guid UserId,
    Guid ParentHabitId,
    string Title,
    string? Description) : IRequest<Result<Guid>>;

public class CreateSubHabitCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<User> userRepository,
    IUserDateService userDateService,
    IUnitOfWork unitOfWork,
    IAppConfigService appConfigService,
    IMemoryCache cache) : IRequestHandler<CreateSubHabitCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateSubHabitCommand request, CancellationToken cancellationToken)
    {
        // Sub-habits are a Pro feature
        var user = await userRepository.FindOneTrackedAsync(u => u.Id == request.UserId, cancellationToken: cancellationToken);
        if (user is not null && !user.HasProAccess)
        {
            return Result.Failure<Guid>("Sub-habits are a Pro feature. Upgrade to unlock!");
        }

        var parent = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.ParentHabitId && h.UserId == request.UserId,
            cancellationToken: cancellationToken);

        if (parent is null)
            return Result.Failure<Guid>("Parent habit not found.");

        // Enforce max nesting depth from config
        var maxDepth = await appConfigService.GetAsync("MaxHabitDepth", 5, cancellationToken);
        var depth = await GetDepthAsync(parent, habitRepository, cancellationToken);
        if (depth >= maxDepth - 1)
            return Result.Failure<Guid>($"Maximum nesting depth reached ({maxDepth} levels).");

        // Use today as dueDate if parent's dueDate has already advanced past today
        var userToday = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var childDueDate = parent.DueDate > userToday ? parent.DueDate : userToday;

        var childResult = Habit.Create(
            request.UserId,
            request.Title,
            parent.FrequencyUnit,
            parent.FrequencyQuantity,
            request.Description,
            dueDate: childDueDate,
            parentHabitId: parent.Id);

        if (childResult.IsFailure)
            return Result.Failure<Guid>(childResult.Error);

        await habitRepository.AddAsync(childResult.Value, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        for (int i = -1; i <= 1; i++)
        {
            cache.Remove($"summary:{request.UserId}:{today.AddDays(i):yyyy-MM-dd}:en");
            cache.Remove($"summary:{request.UserId}:{today.AddDays(i):yyyy-MM-dd}:pt-BR");
        }

        return Result.Success(childResult.Value.Id);
    }

    private static async Task<int> GetDepthAsync(Habit habit, IGenericRepository<Habit> repo, CancellationToken ct)
    {
        var depth = 0;
        var current = habit;
        while (current.ParentHabitId is not null)
        {
            depth++;
            current = await repo.GetByIdAsync(current.ParentHabitId.Value, ct);
            if (current is null) break;
        }
        return depth;
    }

}
