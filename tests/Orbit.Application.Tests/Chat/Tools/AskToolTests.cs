using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat.Tools;

public partial class AskToolTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IUserDateService _dates = Substitute.For<IUserDateService>();
    private readonly AskTool _tool;
    private static readonly Guid UserId = Guid.NewGuid();

    public AskToolTests()
    {
        var habits = Substitute.For<IGenericRepository<Habit>>();
        var planner = new BulkHabitReplayPlanner(Substitute.For<IIdempotencyContext>(),
            Substitute.For<IIdempotencyStore>(), Substitute.For<IUnitOfWork>());
        _tool = new AskTool(new BulkCreateHabitsTool(_mediator),
            new BulkLogHabitsTool(_mediator, habits, _dates, planner),
            new BulkSkipHabitsTool(_mediator, habits, _dates, planner), _dates);
        _dates.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 4, 3));
        _mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
    }

    [Theory]
    [InlineData("title")]
    [InlineData("multiSelect")]
    [InlineData("allowFreeText")]
    [InlineData("action")]
    [InlineData("items")]
    public async Task OptionalNull_ChoiceBehavesLikeAbsent(string field)
    {
        var body = new Dictionary<string, object?>
        {
            ["form"] = "choice",
            ["question"] = "When?",
            ["options"] = new[] { new { id = "a", label = "Morning" }, new { id = "b", label = "Evening" } }
        };
        var absent = await _tool.ExecuteAsync(JsonSerializer.SerializeToElement(body), UserId, default);
        body[field] = null;
        var explicitNull = await _tool.ExecuteAsync(JsonSerializer.SerializeToElement(body), UserId, default);
        absent.Success.Should().BeTrue();
        explicitNull.Should().BeEquivalentTo(absent);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    public async Task Choice_EnforcesOptionCount(int count, bool valid)
    {
        var args = JsonSerializer.SerializeToElement(new
        {
            form = "choice",
            question = "When?",
            options = Enumerable.Range(0, count).Select(i => new { id = i.ToString(), label = "Option " + i })
        });
        (await _tool.ExecuteAsync(args, UserId, default)).Success.Should().Be(valid);
    }

    [Theory]
    [InlineData("question", 120, true)]
    [InlineData("question", 121, false)]
    [InlineData("label", 20, true)]
    [InlineData("label", 21, false)]
    [InlineData("label", 41, false)]
    [InlineData("description", 24, true)]
    [InlineData("description", 25, false)]
    [InlineData("description", 81, false)]
    public async Task Choice_EnforcesTextLimits(string field, int count, bool valid)
    {
        var args = JsonSerializer.SerializeToElement(new
        {
            form = "choice",
            question = field == "question" ? new string('q', count) : "When?",
            options = new[] { new { id = "a", label = field == "label" ? new string('l', count) : "Morning",
                description = field == "description" ? new string('d', count) : "Early" },
                new { id = "b", label = "Evening", description = "Later" } }
        });
        (await _tool.ExecuteAsync(args, UserId, default)).Success.Should().Be(valid);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(24, true)]
    [InlineData(25, false)]
    public async Task PickList_EnforcesDetailLimitWithoutCappingHabitLabel(int? count, bool valid)
    {
        var label = new string('l', 100);
        var detail = count.HasValue ? new string('d', count.Value) : null;
        var args = JsonSerializer.SerializeToElement(new
        {
            form = "pickList",
            title = "Choose",
            action = "create_habits",
            items = new[] { new { id = "a", label, detail, arguments = new { title = "Walk" } } }
        });

        var result = await _tool.ExecuteAsync(args, UserId, default);

        result.Success.Should().Be(valid);
        if (valid)
        {
            var item = result.Payload.Should().BeOfType<AstraBlockDefinition>().Subject.Items.Should().ContainSingle().Subject;
            item.Label.Should().Be(label);
            item.Detail.Should().Be(detail);
        }
    }

    [Fact]
    public void Schema_DeclaresTextLimitsWithoutCappingPickListLabel()
    {
        var properties = JsonSerializer.SerializeToElement(_tool.GetParameterSchema()).GetProperty("properties");
        var option = properties.GetProperty("options").GetProperty("items").GetProperty("properties");
        option.GetProperty("label").GetProperty("maxLength").GetInt32().Should().Be(20);
        option.GetProperty("description").GetProperty("maxLength").GetInt32().Should().Be(24);
        var item = properties.GetProperty("items").GetProperty("items").GetProperty("properties");
        item.GetProperty("detail").GetProperty("maxLength").GetInt32().Should().Be(24);
        item.GetProperty("label").TryGetProperty("maxLength", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("create_habits")]
    [InlineData("log_habits")]
    [InlineData("skip_habits")]
    public async Task PickList_ChecksEachItemAndCombinedArgumentsWithoutWriting(string action)
    {
        var items = Enumerable.Range(0, 2).Select(i => new
        {
            id = i.ToString(),
            label = "Item " + i,
            arguments = action == "create_habits" ? JsonSerializer.SerializeToElement(new { title = "Habit " + i })
                : JsonSerializer.SerializeToElement(new { habit_id = Guid.NewGuid(), date = "2026-04-03" })
        });
        var result = await _tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { form = "pickList", title = "Choose", action, items }), UserId, default);
        result.Success.Should().BeTrue();
        var definition = result.Payload.Should().BeOfType<AstraBlockDefinition>().Subject;
        definition.Items.Should().HaveCount(2);
        if (action == "create_habits")
            definition.Items.Should().OnlyContain(item => item.Arguments.GetProperty("due_date").GetString() == "2026-04-03");
        await _mediator.Received(3).Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>());
        _mediator.ReceivedCalls().Should().OnlyContain(call => call.GetArguments()[0] is CheckChatCommandQuery);
    }

    [Theory]
    [InlineData("create_habits", "{\"title\":\"\"}")]
    [InlineData("log_habits", "{\"habit_id\":\"bad\",\"date\":\"2026-04-03\"}")]
    [InlineData("skip_habits", "{\"habit_id\":\"bad\",\"date\":\"2026-04-03\"}")]
    public async Task PickList_RejectsArgumentsTheBulkToolRejects(string action, string arguments)
    {
        var result = await _tool.ExecuteAsync(JsonSerializer.SerializeToElement(new
        {
            form = "pickList",
            title = "Choose",
            action,
            items = new[] { new { id = "a", label = "Bad", arguments = JsonDocument.Parse(arguments).RootElement } }
        }), UserId, default);
        result.Success.Should().BeFalse();
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    public async Task PickList_EnforcesItemCount(int count, bool valid)
    {
        var items = Enumerable.Range(0, count).Select(i => new { id = i.ToString(), label = "Item", arguments = new { title = "Habit " + i } });
        var result = await _tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { form = "pickList", title = "Choose", action = "create_habits", items }), UserId, default);
        result.Success.Should().Be(valid);
    }

    [Fact]
    public async Task PickList_PropagatesBulkCommandValidationFailure()
    {
        _mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Failure("Habit limit reached."));
        var result = await _tool.ExecuteAsync(JsonDocument.Parse("""{"form":"pickList","title":"Choose","action":"create_habits","items":[{"id":"a","label":"Walk","arguments":{"title":"Walk"}}]}""").RootElement, UserId, default);
        result.Success.Should().BeFalse();
        result.Error.Should().Be("Habit limit reached.");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"form\":\"choice\",\"question\":\"When?\",\"options\":[null,null]}")]
    public async Task MalformedArguments_FailWithoutThrowing(string json) =>
        (await _tool.ExecuteAsync(JsonDocument.Parse(json).RootElement, UserId, default)).Success.Should().BeFalse();

    [Fact]
    public void Metadata_DeclaresBothForms()
    {
        JsonSerializer.Serialize(_tool.GetParameterSchema()).Should().Contain("pickList").And.Contain("choice");
    }
}
