using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Goals.Commands;

public record UpdateGoalCommand(
    Guid UserId,
    Guid GoalId,
    string Title,
    string? Description,
    decimal TargetValue,
    string Unit,
    DateOnly? Deadline) : IRequest<Result>;

public class UpdateGoalCommandHandler(
    IGenericRepository<Goal> goalRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<UpdateGoalCommand, Result>
{
    public async Task<Result> Handle(UpdateGoalCommand request, CancellationToken cancellationToken)
    {
        var goal = await goalRepository.FindOneTrackedAsync(
            g => g.Id == request.GoalId && g.UserId == request.UserId,
            cancellationToken: cancellationToken);

        if (goal is null)
            return Result.Failure(ErrorMessages.GoalNotFound);

        var result = goal.Update(request.Title, request.Description, request.TargetValue, request.Unit, request.Deadline);
        if (result.IsFailure) return result;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
