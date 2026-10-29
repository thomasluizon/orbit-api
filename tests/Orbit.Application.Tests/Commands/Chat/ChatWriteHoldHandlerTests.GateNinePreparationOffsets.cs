using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task GateNineOffsets_CapturedPreparationKeepsArgumentsAndReceivesOffsetGuidance(int repeat)
    {
        ConfigureOffsetReplay("pt-BR");
        var call = OffsetCapture($"composition-medical-batch-pt-BR-r{repeat}")[0];
        var arguments = call.GetProperty("arguments");
        ScriptToolCall(arguments.GetRawText(), call.GetProperty("name").GetString()!);

        var result = await CreateHandler().Handle(new(_userId,
            "dia 01/11, exame de colesterol as 15h50, domiciliar dia 05/11, outros exames de sangue as 13h30, domiciliar PRO EXAME DO DIA 05/11: 3 dias antes, parar com multivitaminico 24 horas antes, nao ir pro muay thay ficar em jejum puro até a coleta",
            ClientContext: new(Locale: "pt-BR", SupportsParts: true, SupportsPendingOperationChanges: true)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var pending = result.Value.PendingOperations.Should().ContainSingle().Which;
        var stored = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        var captured = arguments.GetProperty("habits");
        var held = stored.Arguments.GetProperty("habits");
        pending.Items.Should().HaveCount(captured.GetArrayLength());
        for (var index = 0; index < captured.GetArrayLength(); index++)
            foreach (var field in captured[index].EnumerateObject().Where(field =>
                field.Value.ValueKind != JsonValueKind.Null && field.Name is "title" or "due_date" or "end_date"
                    or "due_time" or "due_end_time" or "frequency_unit" or "frequency_quantity"))
            {
                JsonElement.DeepEquals(held[index].GetProperty(field.Name), field.Value).Should().BeTrue(field.Name);
                pending.Items![index].Fields.Should().Contain(value => value.Field == field.Name && value.NewValue == field.Value.ToString());
            }
        AssertPreparationGuidance(_aiIntentService.ReceivedCalls().SelectMany(call => call.GetArguments()).OfType<AiToolRequest>().Single());
        result.Value.AiMessage.Should().BeNullOrEmpty();
        result.Value.Parts.Should().BeNullOrEmpty();
        result.Value.Actions.Should().BeEmpty();
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GateNineOffsets_RevisionStopsWhenDeclaredComplete(bool completeOnFirstCall)
    {
        ConfigureOffsetReplay("en");
        var initial = OffsetCapture("composition-medical-batch-en-r1").EnumerateArray()
            .Single(call => call.GetProperty("name").GetString() == "bulk_create_habits");
        ScriptToolCall(initial.GetProperty("arguments").GetRawText(), initial.GetProperty("name").GetString()!);
        var client = new AgentClientContext(Locale: "en", SupportsParts: true, SupportsPendingOperationChanges: true);
        var first = await CreateHandler().Handle(new(_userId, "Create the exams and preparation commitments", ClientContext: client), default);
        var pending = first.Value.PendingOperations.Should().ContainSingle().Which;
        var before = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        var capture = OffsetCapture("composition-medical-correction-en-r1");
        AiResponse Revision(int index)
        {
            var node = JsonNode.Parse(capture[index].GetProperty("arguments").GetRawText())!;
            node["pending_operation_id"] = pending.Id.ToString();
            if (completeOnFirstCall) node[TerminalReadDirective.CompleteArgument] = true;
            return new() { ToolCalls = [new("revise_pending_changes", $"revise-{index}", JsonSerializer.SerializeToElement(node))], ConversationContext = new() };
        }
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(Revision(0)));
        var continuations = 0;
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<IReadOnlyList<AiToolCallResult>>().Should().ContainSingle().Which.Success.Should().BeTrue();
            return Result.Success(++continuations == 1 ? Revision(1) : new AiResponse());
        });
        var events = new List<ChatStreamEvent>();

        var result = await CreateHandler().Handle(new(_userId, "Make the cholesterol exam 16:00 instead; keep everything else",
            ClientContext: client, StreamSink: value => { events.Add(value); return Task.CompletedTask; }), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        continuations.Should().Be(completeOnFirstCall ? 0 : 1);
        var revised = result.Value.PendingOperations.Should().ContainSingle().Which;
        revised.Id.Should().Be(pending.Id);
        revised.Items.Should().HaveCount(pending.Items!.Count);
        var after = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        var expected = JsonNode.Parse(before.Arguments.GetRawText())!;
        expected["habits"]![0]!["due_time"] = "16:00";
        JsonElement.DeepEquals(after.Arguments, JsonSerializer.SerializeToElement(expected)).Should().BeTrue();
        _pendingOperationStore.IsAwaitingConfirmation(_userId, pending.Id).Should().BeTrue();
        result.Value.AiMessage.Should().BeNullOrEmpty();
        result.Value.Parts.Should().BeEmpty();
        result.Value.Actions.Should().BeEmpty();
        events.Should().NotContain(value => value.Type == "delta");
        var declaration = _aiIntentService.ReceivedCalls().SelectMany(call => call.GetArguments()).OfType<AiToolRequest>().Last()
            .ToolDeclarations.Select(value => JsonSerializer.SerializeToElement(value))
            .Single(value => value.GetProperty("name").GetString() == "revise_pending_changes");
        declaration.GetProperty("description").GetString().Should().Contain("Set orbit_read_complete=true on the first revision call when it covers the entire correction");
        declaration.GetProperty("parameters").GetProperty("properties").GetProperty(TerminalReadDirective.CompleteArgument)
            .GetProperty("description").GetString().Should().Contain("a complete pending-preview revision")
            .And.Contain("false when a question, read or fresh write still needs another tool call");
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    public async Task GateNineOffsets_IncompleteRevisionContinuesToFreshWrite(string language, bool composition)
    {
        ConfigureOffsetReplay(language);
        if (!composition)
            _featureFlagService.GetEnabledKeysForUserAsync(_userId, Arg.Any<CancellationToken>()).Returns(Array.Empty<string>());
        ScriptToolCall("""{"habits":[{"title":"Read","frequency_unit":"Day","due_time":"21:00"}]}""", "bulk_create_habits");
        var client = new AgentClientContext(Locale: language, SupportsParts: true, SupportsPendingOperationChanges: true);
        var first = await CreateHandler().Handle(new(_userId, "Create a reading habit", ClientContext: client), default);
        var pending = first.Value.PendingOperations.Should().ContainSingle().Which;
        ScriptToolCall(JsonSerializer.Serialize(new
        {
            pending_operation_id = pending.Id,
            answer_question = false,
            items = new[] { new { item_id = pending.Items!.Single().ItemId, edits_json = "{\"due_time\":\"22:00\"}" } },
            orbit_read_complete = false
        }), "revise_pending_changes");
        var continuations = 0;
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            continuations++;
            return Result.Success(new AiResponse
            {
                ToolCalls = [new("bulk_create_habits", "fresh", JsonSerializer.SerializeToElement(new
            {
                habits = new[] { new { title = language == "en" ? "Walk" : "Caminhar", frequency_unit = "Day" } }, orbit_write_complete = true
            }))],
                ConversationContext = new()
            });
        });

        var result = await CreateHandler().Handle(new(_userId, language == "en"
            ? "Make reading 22:00 and create a daily walking habit"
            : "Mude a leitura para 22:00 e crie um hábito diário de caminhar", ClientContext: client), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        continuations.Should().Be(1);
        var combined = result.Value.PendingOperations.Should().ContainSingle().Which;
        combined.Items.Should().HaveCount(2);
        combined.Items.Should().Contain(value => value.EntityName == "Read" && value.Fields.Any(field => field.Field == "due_time" && field.NewValue == "22:00"));
        combined.Items.Should().Contain(value => value.EntityName == (language == "en" ? "Walk" : "Caminhar"));
        result.Value.Actions.Should().BeEmpty();
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    private void ConfigureOffsetReplay(string language)
    {
        ParentAccount(language);
        UseParentHabits([]);
        _userDateService.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 10, 29));
        _featureFlagService.GetEnabledKeysForUserAsync(_userId, Arg.Any<CancellationToken>()).Returns([FeatureFlagKeys.AstraBlockFirst]);
        _promptBuilder.BuildStatic(Arg.Any<PromptBuildRequest>()).Returns(call => new SystemPromptBuilder().BuildStatic(call.Arg<PromptBuildRequest>()));
    }

    private static JsonElement OffsetCapture(string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "GateNineMedical", $"g9i-{name}.json")));
        return document.RootElement.GetProperty("toolCalls").Clone();
    }
}
