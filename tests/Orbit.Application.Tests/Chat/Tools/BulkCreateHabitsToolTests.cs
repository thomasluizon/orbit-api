using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;

namespace Orbit.Application.Tests.Chat.Tools;

public class BulkCreateHabitsToolTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly BulkCreateHabitsTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();

    public BulkCreateHabitsToolTests() => _tool = new BulkCreateHabitsTool(_mediator);

    [Fact]
    public async Task ExplicitOneTime_IsAcceptedByTheBatchArgumentCheck()
    {
        _mediator.Send(Arg.Any<Orbit.Application.Chat.Tools.CheckChatCommandQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var args = JsonSerializer.SerializeToElement(new
        {
            habits = new[] { new { title = "Exam", frequency_unit = "OneTime", due_date = "2026-10-30", due_time = "13:30" } }
        });
        (await _tool.CheckArgumentsAsync(args, UserId, default)).IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData("Day", true)]
    [InlineData("OneTime", false)]
    public async Task ExistingParent_KeepsChildBoundsAndExplicitOneTime(string frequency, bool inherits)
    {
        CreateSubHabitCommand? command = null;
        _mediator.Send(Arg.Do<CreateSubHabitCommand>(value => command = value), Arg.Any<CancellationToken>())
            .Returns(Result.Success(Guid.NewGuid()));
        var args = JsonSerializer.SerializeToElement(new
        {
            parent_habit_id = Guid.NewGuid(),
            habits = new[] { new { title = "Prepare", frequency_unit = frequency, due_date = "2026-10-27", end_date = frequency == "Day" ? "2026-10-30" : null } }
        });
        (await _tool.ExecuteAsync(args, UserId, default)).Success.Should().BeTrue();
        command!.InheritParentFrequency.Should().Be(inherits);
        command.Options!.EndDate.Should().Be(frequency == "Day" ? new DateOnly(2026, 10, 30) : null);
    }

    [Fact]
    public async Task PerItemParents_DispatchChildrenWithIndependentSchedules()
    {
        var commands = new List<CreateSubHabitCommand>();
        _mediator.Send(Arg.Do<CreateSubHabitCommand>(commands.Add), Arg.Any<CancellationToken>())
            .Returns(Result.Success(Guid.NewGuid()));
        var parents = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var args = JsonSerializer.SerializeToElement(new
        {
            habits = new[]
            {
                new { title = "Take vitamin D", parent_habit_id = parents[0], frequency_unit = "Week", frequency_quantity = 1 },
                new { title = "Take rovustatina", parent_habit_id = parents[1], frequency_unit = "Day", frequency_quantity = 1 }
            }
        });

        (await _tool.ExecuteAsync(args, UserId, default)).Success.Should().BeTrue();

        commands.Select(command => command.ParentHabitId).Should().Equal(parents);
        commands.Select(command => command.FrequencyUnit).Should().Equal(
            Orbit.Domain.Enums.FrequencyUnit.Week, Orbit.Domain.Enums.FrequencyUnit.Day);
        await _mediator.DidNotReceive().Send(Arg.Any<BulkCreateHabitsCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NullParent_CreatesTheSameRootsAsAbsentParent()
    {
        var commands = new List<BulkCreateHabitsCommand>();
        _mediator.Send(Arg.Do<BulkCreateHabitsCommand>(commands.Add), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new BulkCreateResult([])));
        (await Execute("""{"habits":[{"title":"Read"}]}""")).Success.Should().BeTrue();
        (await Execute("""{"parent_habit_id":null,"habits":[{"title":"Read"}]}""")).Success.Should().BeTrue();
        commands.Should().HaveCount(2);
        commands[1].Should().BeEquivalentTo(commands[0]);
        commands[1].Habits.Should().ContainSingle(item => item.Title == "Read");
    }

    [Theory]
    [MemberData(nameof(OptionalParameterCases.Fields), typeof(BulkCreateHabitsTool), "habits.items", MemberType = typeof(OptionalParameterCases))]
    public void OptionalNull_BulkItemHasSameDomainInputsAsAbsent(string field)
    {
        var absent = JsonSerializer.Deserialize<JsonElement>("""{"title":"Read"}""");
        var explicitNull = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["title"] = "Read", [field] = null });
        BulkCreateHabitsTool.ParseBulkHabitItem(explicitNull).Should()
            .BeEquivalentTo(BulkCreateHabitsTool.ParseBulkHabitItem(absent));
    }

    [Fact]
    public void Metadata_And_Schema_AreExposed()
    {
        _tool.Name.Should().Be("bulk_create_habits");
        _tool.Description.Should().NotBeNullOrWhiteSpace();
        _tool.GetParameterSchema().Should().NotBeNull();
    }

    [Fact]
    public async Task MissingHabits_ReturnsError()
    {
        var result = await Execute("{}");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("habits is required");
    }

    [Fact]
    public async Task HabitsNotArray_ReturnsError()
    {
        var result = await Execute("""{"habits": "nope"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("habits is required");
    }

    [Fact]
    public async Task HabitWithoutTitle_ReturnsError()
    {
        var result = await Execute("""{"habits": [{"description": "no title here"}]}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("non-empty title");
        await _mediator.DidNotReceive().Send(Arg.Any<BulkCreateHabitsCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmptyArray_ReturnsNoHabitsError()
    {
        var result = await Execute("""{"habits": []}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("No habits provided");
    }

    [Fact]
    public async Task ValidHabitsWithSubHabits_ReportsSuccessCount()
    {
        BulkCreateHabitsCommand? captured = null;
        _mediator.Send(Arg.Any<BulkCreateHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                captured = callInfo.Arg<BulkCreateHabitsCommand>();
                return Result.Success(new BulkCreateResult(new[]
                {
                    new BulkCreateItemResult(0, BulkItemStatus.Success, Guid.NewGuid(), "Morning routine"),
                    new BulkCreateItemResult(1, BulkItemStatus.Failed, null, "Gym", "duplicate"),
                }));
            });

        var result = await Execute("""
            {"habits": [
              {"title": "Morning routine", "frequency_unit": "day", "frequency_quantity": 1, "interval_weeks": 2,
               "sub_habits": [{"title": "Make bed"}]},
              {"title": "Gym", "is_bad_habit": false}
            ]}
            """);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("1/2 habits created");
        result.Payload.Should().BeOfType<BulkCreateResult>();
        var payload = JsonSerializer.SerializeToElement(result.Payload);
        payload.GetProperty("applied_count").GetInt32().Should().Be(1);
        payload.GetProperty("total_matched").GetInt32().Should().Be(2);
        payload.GetProperty("skipped_count").GetInt32().Should().Be(1);
        payload.GetProperty("partial").GetBoolean().Should().BeTrue();
        captured!.Habits.Should().HaveCount(2);
        captured.Habits[0].IntervalWeeks.Should().Be(2);
        captured.Habits[0].SubHabits.Should().ContainSingle().Which.Title.Should().Be("Make bed");
    }

    [Fact]
    public async Task InvalidNestedChild_RejectsWholeRequestBeforeWriting()
    {
        var result = await Execute("""{"habits":[{"title":"Morning routine","sub_habits":[{"description":"missing title"}]}]}""");
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("title");
        await _mediator.DidNotReceive().Send(Arg.Any<BulkCreateHabitsCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CommandFails_PropagatesError()
    {
        _mediator.Send(Arg.Any<BulkCreateHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<BulkCreateResult>("Habit limit reached."));

        var result = await Execute("""{"habits": [{"title": "Read"}]}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Habit limit reached.");
    }

    [Theory]
    [InlineData("""{"title":"Read","frequency_unit":"day"}""", 1)]
    [InlineData("""{"title":"Read","frequency_unit":"week"}""", 1)]
    [InlineData("""{"title":"Read","frequency_unit":"day","frequency_quantity":3}""", 3)]
    [InlineData("""{"title":"Read","frequency_unit":"day","frequency_quantity":0}""", 0)]
    [InlineData("""{"title":"Read"}""", null)]
    public async Task FrequencyQuantity_DefaultsOnlyForRecurringItems(string itemJson, int? expected)
    {
        BulkCreateHabitsCommand? captured = null;
        _mediator.Send(Arg.Any<BulkCreateHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.Arg<BulkCreateHabitsCommand>();
                return Result.Success(new BulkCreateResult([]));
            });

        await Execute($"{{\"habits\":[{itemJson}]}}");

        captured.Should().NotBeNull();
        captured!.Habits.Should().ContainSingle().Which.FrequencyQuantity.Should().Be(expected);
    }

    [Fact]
    public async Task RecurringSubHabitWithoutQuantity_DefaultsToOne()
    {
        BulkCreateHabitsCommand? captured = null;
        _mediator.Send(Arg.Any<BulkCreateHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.Arg<BulkCreateHabitsCommand>();
                return Result.Success(new BulkCreateResult([]));
            });

        await Execute("""{"habits":[{"title":"Routine","frequency_unit":"day","sub_habits":[{"title":"Read","frequency_unit":"week"}]}]}""");

        captured.Should().NotBeNull();
        captured!.Habits[0].SubHabits.Should().ContainSingle().Which.FrequencyQuantity.Should().Be(1);
    }

    [Fact]
    public void Schema_AdvertisesEverySupportedHabitDepth()
    {
        var schema = JsonSerializer.SerializeToElement(_tool.GetParameterSchema());
        var node = schema.GetProperty("properties").GetProperty("habits").GetProperty("items");
        for (var depth = 1; depth <= Orbit.Application.Common.AppConstants.MaxHabitDepth; depth++)
        {
            var fields = node.GetProperty("properties");
            fields.GetProperty("title").GetProperty("type").GetString().Should().Be("string");
            fields.TryGetProperty("sub_habits", out var children).Should().Be(depth < Orbit.Application.Common.AppConstants.MaxHabitDepth);
            if (depth < Orbit.Application.Common.AppConstants.MaxHabitDepth) node = children.GetProperty("items");
        }
    }

    private async Task<ToolResult> Execute(string json) =>
        await _tool.ExecuteAsync(JsonDocument.Parse(json).RootElement, UserId, CancellationToken.None);
}
