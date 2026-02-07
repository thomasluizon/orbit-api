using MediatR;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tasks.Queries;

public record GetTasksQuery(Guid UserId, bool IncludeCompleted = false) : IRequest<IReadOnlyList<TaskItem>>;

public class GetTasksQueryHandler(
    IGenericRepository<TaskItem> taskRepository) : IRequestHandler<GetTasksQuery, IReadOnlyList<TaskItem>>
{
    public async Task<IReadOnlyList<TaskItem>> Handle(GetTasksQuery request, CancellationToken cancellationToken)
    {
        if (request.IncludeCompleted)
        {
            return await taskRepository.FindAsync(
                t => t.UserId == request.UserId,
                cancellationToken);
        }

        return await taskRepository.FindAsync(
            t => t.UserId == request.UserId
                 && t.Status != TaskItemStatus.Completed
                 && t.Status != TaskItemStatus.Cancelled,
            cancellationToken);
    }
}
