using MediatR;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
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
    IPayGateService payGate,
    IGamificationService gamificationService,
    IUnitOfWork unitOfWork,
    ILogger<CreateGoalCommandHandler> logger) : IRequestHandler<CreateGoalCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateGoalCommand request, CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanCreateGoals(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck.PropagateError<Guid>();

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

        // Gamification: process goal creation
        try
        {
            await gamificationService.ProcessGoalCreated(request.UserId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Gamification processing failed for goal creation by user {UserId}", request.UserId);
        }

        return Result.Success(goal.Id);
    }
}
