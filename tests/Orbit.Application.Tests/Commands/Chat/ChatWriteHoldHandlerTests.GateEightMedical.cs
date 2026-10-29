using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Common;
using Orbit.Application.Tests.Chat;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests
{
    [Theory]
    [InlineData("en", 1)]
    [InlineData("en", 2)]
    [InlineData("en", 3)]
    [InlineData("pt-BR", 1)]
    [InlineData("pt-BR", 2)]
    [InlineData("pt-BR", 3)]
    public async Task GateEight_CapturedMedicalArgumentsRemainVisibleWithoutServerRewriting(string language, int repeat)
    {
        ParentAccount(language);
        UseParentHabits([]);
        _userDateService.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(GateSevenToday);
        _featureFlagService.GetEnabledKeysForUserAsync(_userId, Arg.Any<CancellationToken>())
            .Returns([FeatureFlagKeys.AstraBlockFirst]);
        _promptBuilder.BuildStatic(Arg.Any<PromptBuildRequest>())
            .Returns(call => new SystemPromptBuilder().BuildStatic(call.Arg<PromptBuildRequest>()));
        var captured = GateEightCapture($"composition-medical-batch-{language}-r{repeat}")
            .GetProperty("toolCalls")[0];
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse
            {
                ToolCalls = [new(captured.GetProperty("name").GetString()!, "medical", captured.GetProperty("arguments").Clone())],
                ConversationContext = new()
            }));

        var result = await CreateHandler().Handle(new(_userId, language == "en"
            ? HabitProposalValidatorTests.EnglishExamMessage : HabitProposalValidatorTests.PortugueseExamMessage,
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true, Locale: language)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.AiMessage.Should().BeNullOrEmpty();
        result.Value.Parts.Should().BeNullOrEmpty();
        var pending = result.Value.PendingOperations.Should().ContainSingle().Which;
        pending.Items.Should().HaveCount(5);
        var arguments = _pendingOperationStore.GetExecution(_userId, pending.Id)!.Arguments.GetProperty("habits");
        var original = captured.GetProperty("arguments").GetProperty("habits");
        for (var index = 0; index < original.GetArrayLength(); index++)
        {
            var item = pending.Items![index];
            foreach (var field in original[index].EnumerateObject().Where(field => field.Value.ValueKind != JsonValueKind.Null
                && field.Name is "title" or "due_date" or "end_date" or "due_time" or "due_end_time"
                    or "frequency_unit" or "frequency_quantity" or "is_bad_habit"))
            {
                arguments[index].TryGetProperty(field.Name, out var value).Should().BeTrue(field.Name);
                JsonElement.DeepEquals(value, field.Value).Should().BeTrue(field.Name);
                var display = field.Value.ValueKind == JsonValueKind.True ? "True"
                    : field.Value.ValueKind == JsonValueKind.False ? "False" : field.Value.ToString();
                item.Fields.Should().Contain(change => change.Field == field.Name && change.NewValue == display);
            }
        }
        var sent = _aiIntentService.ReceivedCalls().SelectMany(call => call.GetArguments()).OfType<AiToolRequest>().Single();
        sent.SystemPrompt.Should().Contain("Bind each preparation offset to its own activity and referenced event")
            .And.Contain("A fasting deadline stays on the collection date");
        sent.DynamicContext.Should().Contain("For medical instruction titles, keep the person's own action wording");
        foreach (var declaration in sent.ToolDeclarations.Select(value => JsonSerializer.SerializeToElement(value))
            .Where(value => value.GetProperty("name").GetString() is "create_habit" or "bulk_create_habits" or "create_sub_habit"))
        {
            var properties = declaration.GetProperty("parameters").GetProperty("properties");
            if (properties.TryGetProperty("habits", out var habits))
                properties = habits.GetProperty("items").GetProperty("properties");
            properties.GetProperty("title").GetProperty("description").GetString().Should().Contain("person's medical instruction wording");
            properties.GetProperty("frequency_unit").GetProperty("description").GetString().Should().Contain("Ongoing avoid habits default to Day/1");
        }
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        await _aiIntentService.DidNotReceive().ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }
}
