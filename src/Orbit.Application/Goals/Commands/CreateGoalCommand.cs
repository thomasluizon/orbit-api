using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Goals.Commands;

public record CreateGoalCommand(
    Guid UserId,
    string Title,
    string? Description,
    decimal TargetValue,
    string Unit,
    DateOnly? Deadline,
    int Position = 0) : IRequest<Result<Guid>>;

public class CreateGoalCommandHandler(
    IGenericRepository<Goal> goalRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<CreateGoalCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateGoalCommand request, CancellationToken cancellationToken)
    {
        var goalResult = Goal.Create(
            request.UserId,
            request.Title,
            request.TargetValue,
            request.Unit,
            request.Description,
            request.Deadline,
            request.Position);

        if (goalResult.IsFailure)
            return Result.Failure<Guid>(goalResult.Error);

        var goal = goalResult.Value;
        await goalRepository.AddAsync(goal, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(goal.Id);
    }
}
