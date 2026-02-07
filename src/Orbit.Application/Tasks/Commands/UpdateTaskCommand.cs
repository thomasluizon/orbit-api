using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tasks.Commands;

public record UpdateTaskCommand(
    Guid UserId,
    Guid TaskId,
    TaskItemStatus NewStatus) : IRequest<Result>;

public class UpdateTaskCommandHandler(
    IGenericRepository<TaskItem> taskRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<UpdateTaskCommand, Result>
{
    public async Task<Result> Handle(UpdateTaskCommand request, CancellationToken cancellationToken)
    {
        var task = await taskRepository.GetByIdAsync(request.TaskId, cancellationToken);

        if (task is null)
            return Result.Failure("Task not found.");

        if (task.UserId != request.UserId)
            return Result.Failure("Task does not belong to this user.");

        var result = request.NewStatus switch
        {
            TaskItemStatus.Completed => task.MarkCompleted(),
            TaskItemStatus.Cancelled => task.Cancel(),
            TaskItemStatus.InProgress => task.StartProgress(),
            _ => Result.Failure($"Cannot transition to status: {request.NewStatus}")
        };

        if (result.IsSuccess)
        {
            taskRepository.Update(task);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}
