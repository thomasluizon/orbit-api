using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public partial class ProcessUserChatCommandHandlerTests
{
    [Theory]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    public async Task PickListReplay_SparseCreateArgumentsPreserveDraftsAndOneRepair(string language, bool repair)
    {
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        EnableComposition();
        SetupSelectionHabits(CreateHabit("Existing"));
        var title = language == "en" ? "Read" : "Ler";
        var draft = new
        {
            title,
            description = title,
            due_time = "07:00",
            frequency_unit = "Day",
            frequency_quantity = 1,
            checklist_items = new[] { new { text = title } },
            sub_habits = new[] { new { title = title + " 2" } }
        };
        AiToolCall Ask(bool invalid) => new("ask", invalid ? "invalid" : "valid", JsonSerializer.SerializeToElement(new
        {
            input = new
            {
                form = "pickList",
                missing_input = "habit_setup",
                title,
                action = "create_habits",
                items = new[] { new { id = "draft", label = title,
                    arguments = invalid ? "{" : JsonSerializer.Serialize(draft) } }
            }
        }));
        SetupAiResponse(new AiResponse { ConversationContext = TestConversationContext, ToolCalls = [AskSelectionRead(), Ask(repair)] });
        _mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { ConversationContext = TestConversationContext, ToolCalls = [Ask(false)] }));
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(RealAskTool(), query);
        UseProductionReadExecutor(query);

        var result = await handler.Handle(new(UserId, "Prepare the requested selection.", ClientContext: new(
            SupportsParts: true, PartKinds: ["pickList"], Locale: language)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>()
            .Which.Block.Should().BeOfType<AstraPickListBlock>().Which.Action.Should().Be("create_habits");
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        result.Value.Actions.Should().BeEmpty();
        await _blockStore.Received(1).AddAsync(Arg.Is<IssuedAstraBlock>(stored =>
            stored.ReadDefinition().Items.Single().Arguments.GetProperty("title").GetString() == title
            && stored.ReadDefinition().Items.Single().Arguments.GetProperty("due_time").GetString() == "07:00"
            && stored.ReadDefinition().Items.Single().Arguments.GetProperty("sub_habits")[0].GetProperty("title").GetString() == title + " 2"
            && stored.ReadDefinition().Items.Single().Arguments.GetProperty("checklist_items")[0].GetProperty("text").GetString() == title),
            Arg.Any<CancellationToken>());
        await _aiIntentService.Received(repair ? 1 : 0).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        _mediator.ReceivedCalls().Should().OnlyContain(call => call.GetArguments()[0] is CheckChatCommandQuery);
    }

    [Theory]
    [InlineData(1, "pt-BR")]
    [InlineData(2, "pt-BR")]
    [InlineData(3, "pt-BR")]
    [InlineData(1, "en")]
    [InlineData(2, "en")]
    [InlineData(3, "en")]
    public async Task PickListReplay_CapturedAskIssuesSelectionWithoutPreparingWrites(int repeat, string language)
    {
        var user = User.Create("Reader", "reader@example.test").Value;
        user.SetLanguage(language);
        SetupUserAndPayGate(user);
        EnableComposition();
        var response = HonestyReplay("composition-pickList-pt-BR-r" + repeat);
        var call = response.ToolCalls!.Single();
        var arguments = JsonNode.Parse(call.Args.GetRawText())!.AsObject();
        if (language == "en")
        {
            arguments["question"] = repeat == 2 ? null : "Which habits would you like to complete?";
            arguments["title"] = "Choose habits";
            arguments["intro"] = "Choose habits to prepare a preview. Nothing will be completed yet.";
        }
        var items = arguments["items"]!.AsArray();
        var habits = items.Select((item, index) => CreateHabit(language == "en"
            ? index == 0 ? "Water" : "Walk"
            : item!["label"]!.GetValue<string>())).ToArray();
        for (var index = 0; index < items.Count; index++)
        {
            items[index]!["id"] = habits[index].Id.ToString();
            items[index]!["arguments"]!["habit_id"] = habits[index].Id.ToString();
            if (language == "en")
            {
                items[index]!["label"] = habits[index].Title;
                if (items[index]!["detail"] is not null) items[index]!["detail"] = "Due today";
            }
        }
        var replayArguments = JsonSerializer.SerializeToElement(arguments);
        SetupAiResponse(response with { ToolCalls = [AskSelectionRead(), AskWithMissingInput(call with { Args = replayArguments }, "bulk_selection")] });
        _mediator.Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { TextMessage = "Selection unavailable." }));

        SetupSelectionHabits(habits);
        var query = new QueryHabitsTool(_habitRepo, _userRepo, _userDateService, _scheduleLogReader);
        var handler = CreateHandler(RealAskTool(), query);
        UseProductionReadExecutor(query);
        var result = await handler.Handle(new(UserId,
            language == "en" ? "Let me pick which of my habits to complete, without acting yet."
                : "Deixe eu escolher quais dos meus hábitos concluir, sem agir ainda.",
            ClientContext: new(SupportsParts: true, PartKinds: ["pickList"])), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var part = result.Value.Parts.Should().ContainSingle().Which.Should().BeOfType<ChatBlockPart>().Subject;
        part.Kind.Should().Be("pickList");
        var block = part.Block.Should().BeOfType<AstraPickListBlock>().Subject;
        block.Title.Should().Be(arguments["title"]!.GetValue<string>());
        block.Action.Should().Be("log_habits");
        block.Items.Select(item => item.Id).Should().Equal(habits.Select(habit => habit.Id.ToString()));
        block.Items.Select(item => item.Label).Should().Equal(habits.Select(habit => habit.Title));
        block.Items.Select(item => item.Detail).Should().Equal(items.Select(item => item!["detail"]?.GetValue<string>()));
        result.Value.AiMessage.Should().BeNullOrEmpty();
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        result.Value.Actions.Should().BeEmpty();
        await _blockStore.Received(1).AddAsync(Arg.Is<IssuedAstraBlock>(stored => stored.Id == block.BlockId
            && stored.ReadDefinition().Intro == null
            && stored.ReadDefinition().Items.All(item => item.Arguments.GetProperty("date").GetString() == "2026-10-28")),
            Arg.Any<CancellationToken>());
        await _mediator.Received(items.Count + 1).Send(Arg.Any<CheckChatCommandQuery>(), Arg.Any<CancellationToken>());
        _mediator.ReceivedCalls().Should().OnlyContain(invocation => invocation.GetArguments()[0] is CheckChatCommandQuery);
        await AssertOnlyAskSelectionRead();
        await _aiIntentService.DidNotReceiveWithAnyArgs().ContinueWithToolResultsAsync(default!, default!, default, default);
    }
}
