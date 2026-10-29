using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Validators;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests
{
    private static JsonElement GateEightCapture(string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "GateEight", name + ".json")));
        return document.RootElement.Clone();
    }

    private void ReplayGateEightAvoid(string language, int repeat)
    {
        ParentAccount(language);
        UseParentHabits([]);
        _userDateService.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(GateSevenToday);
        _featureFlagService.GetEnabledKeysForUserAsync(_userId, Arg.Any<CancellationToken>())
            .Returns([Orbit.Application.Common.FeatureFlagKeys.AstraBlockFirst]);
        var calls = GateEightCapture($"composition-avoid-form-{language}-r{repeat}")
            .GetProperty("toolCalls").EnumerateArray().Select((call, index) => new AiToolCall(
                call.GetProperty("name").GetString()!, "gate_eight_" + index,
                call.GetProperty("arguments").Clone())).ToArray();
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { ToolCalls = calls, ConversationContext = new() }));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse()));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task GateEight_RejectedAvoidCanRecoverUsingCapturedDailyCall(string language)
    {
        ReplayGateEightAvoid(language, 1);
        var corrected = GateEightCapture($"composition-avoid-form-{language}-r2").GetProperty("toolCalls")[0];
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse
            {
                ToolCalls = [new(corrected.GetProperty("name").GetString()!, "corrected", corrected.GetProperty("arguments").Clone())],
                ConversationContext = new()
            }));

        var result = await CreateHandler().Handle(new(_userId, language == "en" ? "I want to stop smoking" : "quero parar de fumar",
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true, Locale: language)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var item = result.Value.PendingOperations.Should().ContainSingle().Which.Items.Should().ContainSingle().Which;
        item.Fields.Should().Contain(field => field.Field == "frequency_unit" && field.NewValue == "Day");
        item.Fields.Should().Contain(field => field.Field == "frequency_quantity" && field.NewValue == "1");
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Is<IReadOnlyList<AiToolCallResult>>(results => results.Count == 1 && !results[0].Success
                && JsonSerializer.Serialize(results[0].Payload, (JsonSerializerOptions?)null).Contains("Correct the validation error")),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void GateEight_TypedAvoidGuardPreservesExplicitCadenceAndTemporaryTasks()
    {
        var arguments = JsonSerializer.SerializeToElement(new
        {
            habits = new object[]
            {
                new { title = "Drinking soda", is_bad_habit = true, frequency_unit = "Week", frequency_quantity = 2, is_flexible = true },
                new { title = "Stop multivitamins", is_bad_habit = false, frequency_unit = "OneTime", due_date = "2026-10-27" }
            }
        });
        new HabitProposalValidator().Validate(new HabitProposal(string.Empty, arguments)).IsValid.Should().BeTrue();
        Habit.Create(new HabitCreateParams(HabitSchedule.General, _userId, "Smoking", null, null,
            GateSevenToday, IsBadHabit: true)).ErrorCode.Should().Be(DomainErrors.GeneralHabitIsBadHabit.Code);
    }

    [Theory]
    [InlineData("en", 1)]
    [InlineData("en", 3)]
    [InlineData("pt-BR", 1)]
    [InlineData("pt-BR", 3)]
    public async Task GateEight_CapturedGeneralAvoidIsRejectedBeforePreview(string language, int repeat)
    {
        ReplayGateEightAvoid(language, repeat);
        var captured = GateEightCapture($"composition-avoid-form-{language}-r{repeat}")
            .GetProperty("toolCalls")[0].GetProperty("arguments");
        var validation = new HabitProposalValidator().Validate(new HabitProposal(string.Empty, captured));

        var result = await CreateHandler().Handle(new(_userId, language == "en" ? "I want to stop smoking" : "quero parar de fumar",
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true, Locale: language)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        validation.Errors.Should().Contain(error => error.ErrorCode == DomainErrors.GeneralHabitIsBadHabit.Code);
        await _aiIntentService.Received(1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Is<IReadOnlyList<AiToolCallResult>>(results => results.Count == 1 && !results[0].Success
                && results[0].Error!.Contains("Day/1")), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("en", "Smoking")]
    [InlineData("pt-BR", "Fumar")]
    public async Task GateEight_CapturedDailyAvoidKeepsCadenceInOnePreview(string language, string title)
    {
        ReplayGateEightAvoid(language, 2);
        _promptBuilder.BuildStatic(Arg.Any<Orbit.Domain.Interfaces.PromptBuildRequest>())
            .Returns(call => new SystemPromptBuilder().BuildStatic(call.Arg<Orbit.Domain.Interfaces.PromptBuildRequest>()));

        var result = await CreateHandler().Handle(new(_userId, language == "en" ? "I want to stop smoking" : "quero parar de fumar",
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true, Locale: language)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.AiMessage.Should().BeNullOrEmpty();
        result.Value.Parts.Should().BeNullOrEmpty();
        var pending = result.Value.PendingOperations.Should().ContainSingle().Which;
        var item = pending.Items.Should().ContainSingle().Which;
        item.EntityName.Should().Be(title);
        item.Fields.Should().Contain(field => field.Field == "frequency_unit" && field.NewValue == "Day");
        item.Fields.Should().Contain(field => field.Field == "frequency_quantity" && field.NewValue == "1");
        item.Fields.Should().Contain(field => field.Field == "is_bad_habit" && field.NewValue == "True");
        var sent = _aiIntentService.ReceivedCalls().SelectMany(call => call.GetArguments()).OfType<AiToolRequest>().Single();
        sent.SystemPrompt.Should().Contain("An ongoing avoid habit defaults to frequency_unit=Day, frequency_quantity=1 and is_general=false");
        await _aiIntentService.DidNotReceive().ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }
}
