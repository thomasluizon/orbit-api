using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests
{
    public static IEnumerable<object[]> StartingStepCaptures() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "StartingSteps"), "*.json")
            .Order().Select(path => new object[] { Path.GetFileName(path) });

    [Theory]
    [MemberData(nameof(StartingStepCaptures))]
    public async Task OneSmallStep_CapturedCallsRecoverWithinTheTurn(string capture)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "StartingSteps", capture)));
        var call = document.RootElement.GetProperty("toolCalls")[0];
        var args = call.GetProperty("arguments");
        var language = args.GetProperty("language").GetString()!;
        _user.SetLanguage(language);
        _featureFlagService.GetEnabledKeysForUserAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new[] { FeatureFlagKeys.AstraBlockFirst });
        ScriptToolCall(args.GetRawText(), call.GetProperty("name").GetString()!);
        var corrected = ScriptedStartingStepCorrection(args, capture.StartsWith("composition-coaching", StringComparison.Ordinal));
        var errors = new List<AiToolCallResult>();
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(invocation =>
            {
                errors.AddRange(invocation.ArgAt<IReadOnlyList<AiToolCallResult>>(1));
                return Result.Success(new AiResponse { ToolCalls = [corrected], ConversationContext = new() });
            });
        var legacy = capture.Contains("legacy", StringComparison.Ordinal);
        var result = await CreateHandler().Handle(new(_userId, "Help me begin",
            ClientContext: legacy ? null : new(SupportsParts: true, PartKinds: ["oneSmallStep"])), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var expectedStep = args.GetProperty("suggested_sub_habits").GetArrayLength() == 0
            ? corrected.Args.GetProperty("suggested_sub_habits")[0].GetProperty("title").GetString()
            : args.GetProperty("suggested_sub_habits")[0].GetProperty("title").GetString();
        if (legacy)
            result.Value.AiMessage.Should().Be(expectedStep);
        else
        {
            var card = StartingStepCard(result.Value);
            card.Suggestion.Should().Be(new StartingStep(args.GetProperty("title").GetString()!, expectedStep!, language));
            card.Actions.Select(action => action.Id).Should().Equal("smaller", "saveTask", "saveChecklistItem");
            result.Value.AiMessage.Should().BeNull();
        }
        if (call.GetProperty("resultStatus").GetString() == "Failed")
            errors.Should().ContainSingle().Which.Should().Match<AiToolCallResult>(error =>
                !error.Success && error.Error!.Contains("exactly one", StringComparison.Ordinal));
        else
            errors.Should().BeEmpty();
        result.Value.Actions.Should().BeEmpty();
        result.Value.Operations.Should().ContainSingle().Which.Status.Should().Be(AgentOperationStatus.Succeeded);
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        await _habitRepo.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    private static AiToolCall ScriptedStartingStepCorrection(JsonElement args, bool coaching = false)
    {
        var english = args.GetProperty("language").GetString() == "en";
        var source = english
            ? coaching ? "composition-coaching-advice-en-r1.json" : "composition-one-small-step-legacy-en-r1.json"
            : "composition-one-small-step-legacy-pt-BR-r1.json";
        using var capture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "StartingSteps", source)));
        var step = coaching && !english
            ? JsonSerializer.SerializeToElement(new[] { new { title = "Calçar os tênis" } })
            : capture.RootElement.GetProperty("toolCalls")[0].GetProperty("arguments").GetProperty("suggested_sub_habits");
        return new("suggest_breakdown", "corrected", JsonSerializer.SerializeToElement(new
        {
            one_small_step = true,
            language = args.GetProperty("language").GetString(),
            title = args.GetProperty("title").GetString(),
            suggested_sub_habits = step
        }));
    }

    [Theory]
    [InlineData("write")]
    [InlineData("ordinaryBreakdown")]
    [InlineData("extraCall")]
    [InlineData("emptyStep")]
    [InlineData("noCall")]
    [InlineData("providerError")]
    public async Task OneSmallStep_CorrectionIsBoundedAndCannotBecomeAWrite(string continuation)
    {
        using var capture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "StartingSteps", "composition-one-small-step-en-r1.json")));
        var args = capture.RootElement.GetProperty("toolCalls")[0].GetProperty("arguments");
        ScriptToolCall(args.GetRawText(), "suggest_breakdown");
        var create = new AiToolCall("create_habit", "write", JsonSerializer.SerializeToElement(new
        {
            title = "Unrequested task",
            frequency_unit = "OneTime",
            due_date = Today.ToString("yyyy-MM-dd")
        }));
        var calls = continuation switch
        {
            "write" => new[] { create },
            "extraCall" => new[] { ScriptedStartingStepCorrection(args), create },
            "ordinaryBreakdown" => [new AiToolCall("suggest_breakdown", "changed_mode",
                JsonSerializer.SerializeToElement(new { title = "Study", suggested_sub_habits = new[] { new { title = "Open the book" } } }))],
            "emptyStep" => [new AiToolCall("suggest_breakdown", "still_empty", args.Clone())],
            _ => Array.Empty<AiToolCall>()
        };
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
                Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(continuation == "providerError" ? Result.Failure<AiResponse>("Unavailable")
                : Result.Success(new AiResponse { ToolCalls = calls, ConversationContext = new() }));

        var result = await CreateHandler().Handle(new(_userId, "Help me begin",
            ClientContext: new(SupportsParts: true, PartKinds: ["oneSmallStep"])), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.PendingOperations.Should().BeNullOrEmpty();
        (result.Value.Parts ?? []).OfType<ChatBlockPart>().Should().BeEmpty();
        result.Value.Operations.Should().NotContain(operation => operation.Status == AgentOperationStatus.Succeeded);
        result.Value.Operations.Should().OnlyContain(operation => operation.OperationId == "suggest_breakdown");
        await _aiIntentService.ReceivedWithAnyArgs(1).ContinueWithToolResultsAsync(default!, default!, default, default);
        await _habitRepo.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }
}
