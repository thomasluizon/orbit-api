using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Goals.Queries;

public record GoalProgressEntryDto(
    decimal Value,
    decimal PreviousValue,
    string? Note,
    DateTime CreatedAtUtc);

public record GoalDetailDto(
    Guid Id,
    string Title,
    string? Description,
    decimal TargetValue,
    decimal CurrentValue,
    string Unit,
    GoalStatus Status,
    GoalType Type,
    DateOnly? Deadline,
    int Position,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc,
    decimal ProgressPercentage,
    IReadOnlyList<GoalProgressEntryDto> ProgressHistory,
    List<LinkedHabitDto> LinkedHabits);

public record GetGoalByIdQuery(
    Guid UserId,
    Guid GoalId) : IRequest<Result<GoalDetailDto>>;

public class GetGoalByIdQueryHandler(
    IGenericRepository<Goal> goalRepository,
    IPayGateService payGate,
    IUserDateService userDateService) : IRequestHandler<GetGoalByIdQuery, Result<GoalDetailDto>>
{
    public async Task<Result<GoalDetailDto>> Handle(GetGoalByIdQuery request, CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanAccessGoals(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck.PropagateError<GoalDetailDto>();

        var loaded = await GoalDetailLoader.BuildGoalDetailAsync(
            goalRepository, userDateService, request.GoalId, request.UserId, cancellationToken);
        if (loaded is null)
            return Result.Failure<GoalDetailDto>(ErrorMessages.GoalNotFound);

        return Result.Success(loaded.Dto);
    }
}
