using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public partial class HabitDueDateAdvancementServiceTests
{
    [Fact]
    public async Task EndDateEditBetweenLoadAndWrite_SurvivesWithoutCompletingTheHabit()
    {
        var barrier = new BeforeAdvanceWrite();
        using var factory = new SqliteOrbitDbContextFactory(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite"), barrier);
        var user = User.Create("User", "due-date-race@example.com").Value;
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Bad habit", FrequencyUnit.Day, 1,
            IsBadHabit: true, DueDate: Today.AddDays(-3), EndDate: Today.AddDays(-1))).Value;
        factory.Context.AddRange(user, habit);
        await factory.Context.SaveChangesAsync();
        var edited = false;
        barrier.Edit = async () =>
        {
            using var context = factory.CreateContext();
            var current = await context.Habits.SingleAsync();
            current.Update(new(HabitSchedule.Recurring, current.Title, null, FrequencyUnit.Day, 1, null,
                true, current.NextDueDate, EndDate: Today.AddDays(30))).IsSuccess.Should().BeTrue();
            await context.SaveChangesAsync();
            edited = true;
        };
        await CreateService(factory.Context).AdvanceStaleDueDates(default);
        edited.Should().BeTrue();
        using var readback = factory.CreateContext();
        var actual = await readback.Habits.SingleAsync();
        actual.EndDate.Should().Be(Today.AddDays(30));
        actual.IsCompleted.Should().BeFalse();
        actual.NextDueDate.Should().Be(Today.AddDays(-3));
    }

    private sealed class BeforeAdvanceWrite : DbTransactionInterceptor
    {
        private bool _entered;
        public Func<Task>? Edit { get; set; }
        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            if (!_entered && Edit is { } edit)
            {
                _entered = true;
                await edit();
            }
            return result;
        }
    }
}
