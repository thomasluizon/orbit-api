using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Services;

public sealed partial class PlanCommittedOutcomeTests
{
    [Theory]
    [InlineData(false, "done")]
    [InlineData(true, "failed")]
    public async Task ApprovedBulkLog_MarksThePlanStepFailedOnlyWhenAHabitMissedTheRequestedState(bool secondFails, string status)
    {
        using var fixture = new Fixture();
        var alreadyLogged = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, fixture.UserId, "Water", FrequencyUnit.Day, 1,
            new DateOnly(2026, 11, 7))).Value;
        var fresh = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, fixture.UserId, "Read", FrequencyUnit.Day, 1,
            new DateOnly(2026, 11, 7))).Value;
        fixture.Db.Habits.AddRange(alreadyLogged, fresh);
        await fixture.Db.SaveChangesAsync();
        fixture.BulkLogMediator.Send(Arg.Any<BulkLogHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new BulkLogResult([
                new(0, BulkItemStatus.Success, alreadyLogged.Id),
                secondFails
                    ? new(1, BulkItemStatus.Failed, fresh.Id, Error: "Not scheduled")
                    : new(1, BulkItemStatus.Success, fresh.Id, Guid.NewGuid())])));
        var id = await fixture.ProposeAsync(operationId: "bulk_log_habits",
            toolArguments: new { habit_ids = new[] { alreadyLogged.Id.ToString(), fresh.Id.ToString() }, date = "2026-11-07" });

        var response = await ConfirmAndExecuteAsync(fixture, id);

        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, JsonSerializer.Serialize(response));
        var payload = JsonSerializer.SerializeToElement(response.Operation.Payload);
        payload.GetProperty("partial").GetBoolean().Should().Be(secondFails);
        payload.GetProperty("unchanged_count").GetInt32().Should().Be(1);
        await fixture.Plans.Received(1).ChangeAsync(fixture.UserId, id, status, Arg.Any<CancellationToken>());
        fixture.Db.ChangeTracker.Clear();
        var stored = (await StoredOutcomeAsync(fixture, id)).GetProperty("Execution").GetProperty("payload");
        stored.GetProperty("partial").GetBoolean().Should().Be(secondFails);
        stored.GetProperty("unchanged_count").GetInt32().Should().Be(1);
        _output.WriteLine(JsonSerializer.Serialize(response.Operation, WireOptions));
    }
}
