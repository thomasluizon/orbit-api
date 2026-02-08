using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tasks.Commands;

public record DeleteTaskCommand(Guid UserId, Guid TaskId) : IRequest<Result>;

public class DeleteTaskCommandHandler(
    IGenericRepository<TaskItem> taskRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<DeleteTaskCommand, Result>
{
    public async Task<Result> Handle(DeleteTaskCommand request, CancellationToken cancellationToken)
    {
        var task = await taskRepository.GetByIdAsync(request.TaskId, cancellationToken);

        if (task is null)
            return Result.Failure("Task not found.");

        if (task.UserId != request.UserId)
            return Result.Failure("You don't have permission to delete this task.");

        taskRepository.Remove(task);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
