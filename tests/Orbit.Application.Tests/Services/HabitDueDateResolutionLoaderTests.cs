using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Services;

public sealed class HabitDueDateResolutionLoaderTests
{
    [Fact]
    public async Task LoadAsync_FiltersResolutionLogsThroughHabitOwner()
    {
        var owner = Guid.NewGuid();
        var due = new DateOnly(2026, 1, 1);
        var own = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, owner, "Own", FrequencyUnit.Day, 1, DueDate: due)).Value;
        var foreign = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, Guid.NewGuid(), "Foreign", FrequencyUnit.Day, 1, DueDate: due)).Value;
        var logs = new[] { HabitLog.FromSummaryRead(own.Id, due, 1), HabitLog.FromSummaryRead(foreign.Id, due, 1) };
        var repository = Substitute.For<IGenericRepository<HabitLog>>();
        repository.ProjectAsync(Arg.Any<Expression<Func<HabitLog, bool>>>(),
                Arg.Any<Func<IQueryable<HabitLog>, IQueryable<HabitMetricLog>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<IQueryable<HabitLog>, IQueryable<HabitMetricLog>>>()(
                logs.AsQueryable().Where(call.Arg<Expression<Func<HabitLog, bool>>>())).ToArray());
        var resolved = await HabitDueDateResolutionLoader.LoadAsync(owner, repository, [own, foreign], due.AddDays(60), default);
        resolved.Should().Equal(own.Id);
        await repository.Received(1).ProjectAsync(
            Arg.Is<Expression<Func<HabitLog, bool>>>(predicate => predicate.Compile()(logs[0]) && !predicate.Compile()(logs[1])),
            Arg.Any<Func<IQueryable<HabitLog>, IQueryable<HabitMetricLog>>>(), Arg.Any<CancellationToken>());
    }
}
