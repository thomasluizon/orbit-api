using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Services;

internal static class HabitCreationGates
{
    public static Task<Result> CheckAsync(Habit habit, IPayGateService payGate, CancellationToken ct) =>
        habit.SlipAlertEnabled
            ? payGate.CanUseSlipAlerts(habit.UserId, ct)
            : Task.FromResult(Result.Success());
}
