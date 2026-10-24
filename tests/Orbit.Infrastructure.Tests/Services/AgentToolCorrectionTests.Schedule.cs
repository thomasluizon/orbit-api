using FluentAssertions;
using FluentAssertions.Execution;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Services;

public sealed partial class AgentToolCorrectionTests
{
    [Fact]
    public async Task ScheduleCertification_UnsupportedWeekdaysCannotReachAOneTimeSave()
    {
        const string original = """{"title":"Read","days":["Monday"]}""";
        var tool = (IPreparedIntentTool)_registry.GetTool("create_habit")!;
        (await tool.CheckArgumentsAsync(Parse(original), _userId, default)).IsFailure.Should().BeTrue();

        var response = await _executor.ExecuteAsync(Request(original));
        var proposal = response is RejectedBeforeDispatchResponse evidence
            ? await _executor.ExecuteAsync(evidence.Request with { Arguments = evidence.Intent.ExecutionArguments })
            : response;
        AgentExecuteOperationResponse? executed = null;
        if (proposal.PendingOperation is { } pending)
        {
            var stored = _store.GetExecution(_userId, pending.Id)!;
            var approval = _store.Confirm(_userId, pending.Id)!;
            executed = await _executor.ExecuteAsync(Request(stored.Arguments.GetRawText()) with
            {
                ConfirmationToken = approval.ConfirmationToken
            });
        }

        using var assertions = new AssertionScope();
        response.Should().BeOfType<AgentExecuteOperationResponse>();
        (await tool.PrepareIntentAsync(Parse(original), _userId, default)).Should().BeNull();
        executed?.Operation.Status.Should().Be(AgentOperationStatus.Failed);
        _saved.Should().BeEmpty();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScheduleCertification_ZeroSecondCorrectionRetainsPersistedWeekdaysThroughApproval()
    {
        const string original = """{"title":"Read","frequency_unit":"Day","days":["Monday"],"interval_weeks":2,"due_time":"21:17:00"}""";
        var request = Request(original) with { CorrelationId = "schedule-control", IncludeChangePreview = true };
        var response = await _executor.ExecuteAsync(request);
        var evidence = response.Should().BeOfType<RejectedBeforeDispatchResponse>().Subject;
        evidence.Request.Should().Be(request);
        _db.PendingAgentOperations.Should().BeEmpty();
        _saved.Should().BeEmpty();

        var proposal = await _executor.ExecuteAsync(evidence.Request with { Arguments = evidence.Intent.ExecutionArguments });

        proposal.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
        var pending = proposal.PendingOperation!;
        pending.Items.Should().ContainSingle();
        pending.Items![0].Fields.Should().Contain(field => field.Field == "days" && field.NewValue!.Contains("Monday"));
        _saved.Should().BeEmpty();
        var stored = _store.GetExecution(_userId, pending.Id)!;
        stored.Arguments.GetProperty("due_time").GetString().Should().Be("21:17");
        stored.Arguments.GetProperty("due_date").GetString().Should().Be("2026-10-05");
        _store.Confirm(Guid.NewGuid(), pending.Id).Should().BeNull();
        var approval = _store.Confirm(_userId, pending.Id)!;
        var approved = request with { Arguments = stored.Arguments, ConfirmationToken = approval.ConfirmationToken };

        var executed = await _executor.ExecuteAsync(approved);
        var repeated = await _executor.ExecuteAsync(approved);

        executed.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        repeated.Operation.Status.Should().NotBe(AgentOperationStatus.Succeeded);
        var habit = _saved.Should().ContainSingle().Subject;
        habit.FrequencyUnit.Should().Be(FrequencyUnit.Day);
        habit.FrequencyQuantity.Should().Be(1);
        habit.Days.Should().Equal(DayOfWeek.Monday);
        habit.IntervalWeeks.Should().Be(2);
        habit.NextDueDate.Should().Be(new DateOnly(2026, 10, 5));
        habit.DueTime.Should().Be(new TimeOnly(21, 17));
        _store.GetExecution(_userId, pending.Id).Should().BeNull();
    }

    [Theory]
    [InlineData("""{"title":"Read"}""", true)]
    [InlineData("""{"title":"Read","frequency_unit":null,"due_date":null}""", true)]
    [InlineData("""{"title":"Read","frequency_unit":null,"due_date":"2026-10-05","days":["Monday"],"interval_weeks":2}""", true)]
    [InlineData("""{"title":"Read","frequency_unit":"OneTime"}""", true)]
    [InlineData("""{"title":"Read","interval_weeks":2}""", false)]
    [InlineData("""{"title":"Read","frequency_unit":"Day","sub_habits":[{"title":"Notes"}]}""", true)]
    [InlineData("""{"title":"Read","frequency_unit":"Day","days":["Monday"],"interval_weeks":2,"sub_habits":[{"title":"Notes","frequency_unit":null,"due_date":null}]}""", true)]
    [InlineData("""{"title":"Read","frequency_unit":"Week","days":["Monday"],"interval_weeks":2,"due_date":null,"sub_habits":[{"title":"Notes","due_date":"2026-10-05"}]}""", true)]
    [InlineData("""{"title":"Read","frequency_unit":"Day","days":["Monday"],"sub_habits":[{"title":"Notes","frequency_unit":"Week","days":["Tuesday"],"interval_weeks":3,"due_date":"2026-10-06"},{"title":"Review","frequency_unit":"OneTime","due_date":"2026-10-07"}]}""", true)]
    [InlineData("""{"title":"Read","frequency_unit":"Week","frequency_quantity":3,"is_flexible":true}""", true)]
    [InlineData("""{"title":"Read","is_general":true,"sub_habits":[{"title":"Notes"}]}""", true)]
    [InlineData("""{"title":"Read","frequency_unit":"Day","days":["Monday"],"due_time":"21:17","due_end_time":"22:17","reminder_enabled":true,"reminder_times":[5,30],"checklist_items":[{"text":"Notes","is_checked":true}]}""", true)]
    public async Task ScheduleCertification_DefaultsAndChildInheritancePreserveTheOrdinarySavedDomain(string json, bool certified)
    {
        var arguments = Parse(json);
        var tool = (IPreparedIntentTool)_registry.GetTool("create_habit")!;
        (await tool.CheckArgumentsAsync(arguments, _userId, default)).IsSuccess.Should().BeTrue();
        (await tool.ExecuteAsync(arguments, _userId, default)).Success.Should().BeTrue();
        var expected = _saved.ToArray();
        expected.Should().NotBeEmpty();
        _saved.Clear();
        _unitOfWork.ClearReceivedCalls();
        var prepared = await tool.PrepareIntentAsync(arguments, _userId, default);
        if (certified)
            prepared.Should().NotBeNull();
        else
            prepared.Should().BeNull("materializing a date would discard the ordinary domain's interval");

        var proposal = await _executor.ExecuteAsync(Request(json));

        proposal.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation);
        _saved.Should().BeEmpty();
        var pending = proposal.PendingOperation!;
        var stored = _store.GetExecution(_userId, pending.Id)!;
        var approval = _store.Confirm(_userId, pending.Id)!;
        var executed = await _executor.ExecuteAsync(Request(stored.Arguments.GetRawText()) with
        {
            ConfirmationToken = approval.ConfirmationToken
        });

        executed.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        _saved.Should().HaveCount(expected.Length);
        foreach (var actual in _saved)
        {
            var ordinary = expected.Single(habit => habit.Title == actual.Title);
            actual.Position.Should().Be(ordinary.Position + (actual.ParentHabitId is null ? 1 : 0));
            actual.Should().BeEquivalentTo(expected.Single(habit => habit.Title == actual.Title), options => options
                .ComparingByMembers<Habit>()
                .Excluding(habit => habit.Id).Excluding(habit => habit.ParentHabitId)
                .Excluding(habit => habit.CreatedAtUtc).Excluding(habit => habit.UpdatedAtUtc).Excluding(habit => habit.Position));
        }
        var parent = _saved.Single(habit => habit.ParentHabitId is null);
        _saved.Where(habit => habit != parent).Should().OnlyContain(child => child.ParentHabitId == parent.Id);
    }

    [Theory]
    [InlineData("""{"title":"Read","frequency_unit":null,"days":["Monday"]}""")]
    [InlineData("""{"title":"Read","due_date":null,"days":["Monday"]}""")]
    [InlineData("""{"title":"Read","frequency_unit":null,"due_date":null,"days":["Monday"],"interval_weeks":2}""")]
    [InlineData("""{"title":"Read","sub_habits":[{"title":"Notes","days":["Monday"]}]}""")]
    public async Task ScheduleCertification_UnsupportedOriginalDomainRemainsUncertified(string json)
    {
        var tool = (IPreparedIntentTool)_registry.GetTool("create_habit")!;
        (await tool.CheckArgumentsAsync(Parse(json), _userId, default)).IsFailure.Should().BeTrue();

        (await tool.PrepareIntentAsync(Parse(json), _userId, default)).Should().BeNull();

        _saved.Should().BeEmpty();
        _db.PendingAgentOperations.Should().BeEmpty();
    }
}
