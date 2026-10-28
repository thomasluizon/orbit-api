using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    public async Task BrainDump_ModelReplayProducesOneGroupedPreviewWithoutWriting(string language, bool composition)
    {
        SetupUserAndPayGate();
        if (composition) EnableComposition();
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "BrainDumps", language + ".json"));
        using var original = JsonDocument.Parse(source);
        var title = original.RootElement.GetProperty("existingTitle").GetString()!;
        var existing = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
            UserId, title, FrequencyUnit.Day, 1, Today)).Value;
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns([existing]);
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns([existing]);
        using var fixture = JsonDocument.Parse(source.Replace("__existing__", existing.Id.ToString())
            .Replace("__today__", Today.ToString("yyyy-MM-dd"))
            .Replace("__tomorrow__", Today.AddDays(1).ToString("yyyy-MM-dd")));
        var calls = fixture.RootElement.GetProperty("toolCalls").Deserialize<AiToolCall[]>(JsonSerializerOptions.Web)!;
        var tool = new BulkCreateHabitsTool(_mediator);
        var handler = CreateHandler(tool);
        var held = HoldReplayWrites(tool);
        SetupAiResponse(new AiResponse { ToolCalls = calls, ConversationContext = TestConversationContext });
        ReplyToReplay("There are 3 items ready. Review all 3 items before approving.");

        var result = await handler.Handle(new(UserId, fixture.RootElement.GetProperty("message").GetString()!,
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        held.Should().ContainSingle().Which.OperationId.Should().Be(tool.Name);
        var preview = result.Value.PendingOperations.Should().ContainSingle().Subject;
        preview.Items.Should().HaveCount(3);
        preview.Items![2].EntityId.Should().Be(existing.Id);
        var items = JsonSerializer.SerializeToElement(preview.Items, JsonSerializerOptions.Web);
        items.EnumerateArray().Select(item => item.GetProperty("habitType").GetString())
            .Should().Equal("Task", "Habit", "Habit");
        result.Value.AiMessage.Should().BeNullOrEmpty();
        result.Value.FollowUps.Should().BeNullOrEmpty();
        result.Value.Actions.Should().BeEmpty();
        result.Value.Parts.Should().NotContain(part => part is ChatTextPart);
        existing.DueTime.Should().BeNull();
        await _mediator.DidNotReceive().Send(Arg.Any<BulkCreateHabitsCommand>(), Arg.Any<CancellationToken>());
        await _mediator.DidNotReceive().Send(Arg.Any<UpdateHabitFieldsCommand>(), Arg.Any<CancellationToken>());
        var wire = JsonSerializer.Serialize(result.Value, ChatStreamEvent.SerializerOptions);
        var replay = JsonSerializer.Deserialize<ChatResponse>(wire, ChatStreamEvent.SerializerOptions)!;
        replay.PendingOperations!.Single().Items!.Select(item => item.HabitType)
            .Should().Equal("Task", "Habit", "Habit");
        replay.PendingOperations!.Single().Id.Should().Be(preview.Id);
    }
}
