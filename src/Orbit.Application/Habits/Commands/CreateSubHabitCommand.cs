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
    IUnitOfWork unitOfWork,
    IMemoryCache cache) : IRequestHandler<CreateSubHabitCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateSubHabitCommand request, CancellationToken cancellationToken)
    {
        var parent = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.ParentHabitId && h.UserId == request.UserId,
            cancellationToken: cancellationToken);

        if (parent is null)
            return Result.Failure<Guid>("Parent habit not found.");

        var childResult = Habit.Create(
            request.UserId,
            request.Title,
            parent.FrequencyUnit,
            parent.FrequencyQuantity,
            request.Description,
            dueDate: parent.DueDate,
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
}
