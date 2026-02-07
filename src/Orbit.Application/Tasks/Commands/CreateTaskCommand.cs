using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tasks.Commands;

public record CreateTaskCommand(
    Guid UserId,
    string Title,
    string? Description,
    DateOnly? DueDate) : IRequest<Result<Guid>>;

public class CreateTaskCommandHandler(
    IGenericRepository<TaskItem> taskRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<CreateTaskCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateTaskCommand request, CancellationToken cancellationToken)
    {
        var taskResult = TaskItem.Create(
            request.UserId,
            request.Title,
            request.Description,
            request.DueDate);

        if (taskResult.IsFailure)
            return Result.Failure<Guid>(taskResult.Error);

        await taskRepository.AddAsync(taskResult.Value, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(taskResult.Value.Id);
    }
}
