using MediatR;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

public record MoveHabitParentCommand(
    Guid UserId,
    Guid HabitId,
    Guid? ParentId) : IRequest<Result>;

public class MoveHabitParentCommandHandler(
    IGenericRepository<Habit> habitRepository,
    IUnitOfWork unitOfWork,
    IAppConfigService appConfigService,
    IPayGateService payGate) : IRequestHandler<MoveHabitParentCommand, Result>
{
    public Task<Result> Handle(MoveHabitParentCommand request, CancellationToken cancellationToken) =>
        HabitCeilingLock.ExecuteEntryAsync(
            unitOfWork,
            request.UserId,
            payGate,
            ct => LoadHabitAsync(request, ct),
            habit => HabitLiveRootEntry.FromPromotion(habit, request.ParentId),
            (habit, ct) => MoveAsync(request, habit, ct),
            cancellationToken);

    private async Task<Result<Habit>> LoadHabitAsync(
        MoveHabitParentCommand request,
        CancellationToken cancellationToken)
    {
        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.HabitId && h.UserId == request.UserId,
            q => q.Include(h => h.Children),
            cancellationToken);

        if (habit is null)
            return Result.Failure<Habit>(ErrorMessages.HabitNotFound);

        return Result.Success(habit);
    }

    private async Task<Result> MoveAsync(
        MoveHabitParentCommand request,
        Habit habit,
        CancellationToken cancellationToken)
    {
        if (request.ParentId is null)
        {
            habit.SetParentHabitId(null);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }

        var parent = await habitRepository.FindOneTrackedAsync(
            h => h.Id == request.ParentId && h.UserId == request.UserId,
            cancellationToken: cancellationToken);
        var validation = await ValidateMoveAsync(request, habit, parent, habitRepository, appConfigService, cancellationToken);
        if (validation.IsFailure)
            return validation;

        habit.SetParentHabitId(request.ParentId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    internal static async Task<Result> CheckArgumentsAsync(MoveHabitParentCommand request,
        IGenericRepository<Habit> repository, IAppConfigService config, CancellationToken ct)
    {
        var matches = await repository.FindAsync(h => h.Id == request.HabitId && h.UserId == request.UserId, ct);
        var habit = matches.FirstOrDefault();
        if (habit is null)
            return Result.Failure(ErrorMessages.HabitNotFound);
        if (request.ParentId is null)
            return Result.Success();
        var parents = await repository.FindAsync(h => h.Id == request.ParentId && h.UserId == request.UserId, ct);
        return await ValidateMoveAsync(request, habit, parents.FirstOrDefault(), repository, config, ct);
    }

    internal static async Task<Result> ValidateMoveAsync(MoveHabitParentCommand request, Habit habit, Habit? parent,
        IGenericRepository<Habit> repository, IAppConfigService config, CancellationToken ct, bool? finalIsGeneral = null)
    {
        if (request.ParentId == request.HabitId)
            return Result.Failure(ErrorMessages.SelfParent);
        if (request.ParentId == Guid.Empty || parent is null || parent.IsDeleted || parent.UserId != request.UserId)
            return Result.Failure(ErrorMessages.TargetParentNotFound);

        if (parent.IsGeneral != (finalIsGeneral ?? habit.IsGeneral))
            return Result.Failure(ErrorMessages.GeneralMismatchWithParent);

        var allHabits = await repository.FindAsync(h => h.UserId == request.UserId, ct);
        var parentsById = allHabits.ToDictionary(h => h.Id, h => h.ParentHabitId);

        if (HabitHierarchy.WouldCreateCycle(request.HabitId, parent.Id, parentsById))
            return Result.Failure(ErrorMessages.CircularReference);

        var maxDepth = await config.GetAsync(AppConfigKeys.MaxHabitDepth, AppConstants.MaxHabitDepth, ct);
        var parentDepth = HabitHierarchy.GetDepth(parent.Id, parentsById);
        var subtreeHeight = HabitHierarchy.GetSubtreeHeight(request.HabitId, parentsById);
        if (parentDepth + 1 + subtreeHeight > maxDepth - 1)
            return Result.Failure(ErrorMessages.MaxDepthReached.Format(maxDepth));

        return Result.Success();
    }

}
