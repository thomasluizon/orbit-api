using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests
{
    private const string EnglishToolFailure = "I couldn't complete that. Please try again.";

    [Theory]
    [InlineData("en", "Create Read a book daily and Stretch daily")]
    [InlineData("pt-BR", "Crie Ler um livro todo dia e Alongar todo dia")]
    public async Task PlanOnlyFirstRound_ContinuesToPreviewAndTracesThePlanCall(string language, string message)
    {
        EnableEvalTrace();
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(PlanOnlyResponse()));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(BulkCreateResponse()), Result.Success(new AiResponse { TextMessage = "Confirm to create." }));

        var result = await CreateHandler().Handle(new ProcessUserChatCommand(_userId, message,
            ClientContext: new(Locale: language), EvalTraceRequested: true), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().NotBe(EnglishToolFailure).And.NotBe("Não consegui concluir isso. Tente novamente.");
        result.Value.PendingOperations.Should().ContainSingle().Which.Items.Should().HaveCount(2);
        await _aiIntentService.Received().ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Is<IReadOnlyList<AiToolCallResult>>(results => results.Count == 1 && results[0].Name == "present_plan" && !results[0].Success),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        result.Value.EvalTrace!.ToolCalls.Select(call => call.Name).Should().Equal("present_plan", "bulk_create_habits");
        result.Value.EvalTrace.ToolCalls[0].Status.Should().Be("Failed");
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Orbit.Domain.Entities.Habit>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("en", "Create Read a book daily and Stretch daily")]
    [InlineData("pt-BR", "Crie Ler um livro todo dia e Alongar todo dia")]
    public async Task PlanOnlyRound_RejectionNamesThePlannedActionToolsSoTheNextRoundActs(string language, string message)
    {
        AllowHabitCreation();
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(PlanOnlyResponse()));
        var sent = new List<IReadOnlyList<AiToolCallResult>>();
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Do<IReadOnlyList<AiToolCallResult>>(results => sent.Add([.. results])),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(BulkCreateResponse()), Result.Success(new AiResponse { TextMessage = "Confirm to create." }));

        var result = await CreateHandler().Handle(new ProcessUserChatCommand(_userId, message,
            ClientContext: new(Locale: language)), default);

        result.IsSuccess.Should().BeTrue();
        sent[0].Should().ContainSingle().Which.Error.Should().Be(
            "present_plan arrived without an action call, so the person saw no plan. Call the planned action tools in your next response, without present_plan: bulk_create_habits.");
        result.Value.PendingOperations.Should().ContainSingle().Which.Items.Should().HaveCount(2);
        sent.Should().HaveCount(2, "the plan-only round costs one model call and the action round ends the turn");
    }

    [Fact]
    public async Task PlanOnlyRoundAfterRead_ContinuesToPreviewInPlaceOfTheReadBlock()
    {
        EnableEvalTrace();
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse
            {
                ToolCalls = [new AiToolCall("query_habits", "read", JsonSerializer.SerializeToElement(new { }))],
                ConversationContext = new() { Messages = new List<object>(), Options = new object() }
            }));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(PlanOnlyResponse()), Result.Success(BulkCreateResponse()),
                Result.Success(new AiResponse { TextMessage = "Confirm to create." }));

        var result = await CreateHandler().Handle(new ProcessUserChatCommand(_userId,
            "Organize my day: read a book daily, stretch daily", EvalTraceRequested: true), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiMessage.Should().NotBe(EnglishToolFailure);
        result.Value.PendingOperations.Should().ContainSingle().Which.Items.Should().HaveCount(2);
        result.Value.EvalTrace!.ToolCalls.Select(call => call.Name).Should().Equal("query_habits", "present_plan", "bulk_create_habits");
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Orbit.Domain.Entities.Habit>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlanOnlyRounds_StopAtTheIterationLimit()
    {
        AllowHabitCreation();
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(PlanOnlyResponse()));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
                Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(PlanOnlyResponse()));

        var result = await CreateHandler().Handle(new ProcessUserChatCommand(_userId, "Create two habits"), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.PendingOperations.Should().BeEmpty();
        await _aiIntentService.Received(Orbit.Application.Chat.ChatTurnLimits.MaxToolIterations).ContinueWithToolResultsAsync(
            Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(),
            Arg.Any<CancellationToken>());
    }

    private void EnableEvalTrace()
    {
        _featureFlagService.GetEnabledKeysForUserAsync(_userId, Arg.Any<CancellationToken>()).Returns(new[] { "astra_eval_trace" });
        AllowHabitCreation();
    }

    private void AllowHabitCreation() =>
        _mediator.Send(Arg.Any<Orbit.Application.Chat.Tools.CheckChatCommandQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success());

    private static AiResponse PlanOnlyResponse() => new()
    {
        ToolCalls = [new AiToolCall("present_plan", "plan", JsonSerializer.SerializeToElement(new
        {
            title = "New habits",
            steps = new[]
            {
                new { text = "Prepare the habits", tool = "bulk_create_habits", phase = "execution" },
                new { text = "Save after approval", tool = "bulk_create_habits", phase = "approval" }
            }
        }))],
        ConversationContext = new() { Messages = new List<object>(), Options = new object() }
    };

    private static AiResponse BulkCreateResponse() => new()
    {
        ToolCalls = [new AiToolCall("bulk_create_habits", "create", JsonSerializer.SerializeToElement(new
        {
            habits = new[]
            {
                new { title = "Read a book", frequency_unit = "Day", frequency_quantity = 1 },
                new { title = "Stretch", frequency_unit = "Day", frequency_quantity = 1 }
            }
        }))],
        ConversationContext = new() { Messages = new List<object>(), Options = new object() }
    };
}
