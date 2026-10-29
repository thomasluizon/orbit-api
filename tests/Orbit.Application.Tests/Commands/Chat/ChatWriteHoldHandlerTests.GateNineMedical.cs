using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Tags.Commands;
using Orbit.Application.Tags.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests
{
    [Theory]
    [InlineData("en", 1, 0, false, true)]
    [InlineData("en", 2, 0, true, true)]
    [InlineData("en", 3, 0, false, true)]
    [InlineData("pt-BR", 2, 0, false, false)]
    [InlineData("pt-BR", 3, 0, true, true)]
    [InlineData("en", 2, 64, true, true)]
    [InlineData("pt-BR", 3, 64, true, true)]
    public async Task GateNine_MedicalPreviewAndApprovedCommandPreserveCapturedBounds(
        string language, int repeat, int shiftDays, bool boundedRestriction, bool collectionDeadline)
    {
        ParentAccount(language);
        UseParentHabits([]);
        using var capture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "GateNineMedical", $"composition-medical-batch-{language}-r{repeat}.json")));
        var call = capture.RootElement.GetProperty("toolCalls")[0];
        var args = ShiftMedicalDates(call.GetProperty("arguments"), shiftDays, language);
        var items = args.GetProperty("habits");
        var eventDate = DateOnly.Parse(items[1].GetProperty("due_date").GetString()!, CultureInfo.InvariantCulture);
        _userDateService.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(eventDate.AddDays(-1));
        _featureFlagService.GetEnabledKeysForUserAsync(_userId, Arg.Any<CancellationToken>())
            .Returns([FeatureFlagKeys.AstraBlockFirst]);
        _promptBuilder.BuildStatic(Arg.Any<PromptBuildRequest>())
            .Returns(request => new SystemPromptBuilder().BuildStatic(request.Arg<PromptBuildRequest>()));
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse
            {
                ToolCalls = [new(call.GetProperty("name").GetString()!, "medical", args)],
                ConversationContext = new()
            }));
        var written = new List<BulkHabitItem>();
        _mediator.Send(Arg.Do<BulkCreateHabitsCommand>(value => written.AddRange(value.Habits)), Arg.Any<CancellationToken>())
            .Returns(request => Result.Success(new BulkCreateResult(request.Arg<BulkCreateHabitsCommand>().Habits
                .Select((habit, index) => new BulkCreateItemResult(index, BulkItemStatus.Success, Guid.NewGuid(), habit.Title)).ToArray())));
        _mediator.Send(Arg.Do<CreateHabitCommand>(value => written.Add(new(value.Title, value.Description,
            value.FrequencyUnit, value.FrequencyQuantity, IsBadHabit: value.IsBadHabit, DueDate: value.DueDate,
            DueTime: value.Options?.DueTime, DueEndTime: value.Options?.DueEndTime, EndDate: value.Options?.EndDate))),
            Arg.Any<CancellationToken>()).Returns(Result.Success(Guid.NewGuid()));
        _mediator.Send(Arg.Any<GetTagsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<TagResponse>>([]));
        _mediator.Send(Arg.Any<AssignTagsCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var locale = CultureInfo.GetCultureInfo(language == "en" ? "en-US" : language);
        var firstDate = DateOnly.Parse(items[0].GetProperty("due_date").GetString()!, CultureInfo.InvariantCulture).ToString("d", locale);
        var secondDate = eventDate.ToString("d", locale);
        var message = language == "en"
            ? $"On {firstDate}, cholesterol exam at 15:50, at home. On {secondDate}, other blood tests at 13:30, at home. For the exam on {secondDate}: 3 days before, stop multivitamins; 24 hours before, don't go to Muay Thai; fast until the collection."
            : $"Dia {firstDate}, exame de colesterol às 15h50, domiciliar. Dia {secondDate}, outros exames de sangue às 13h30, domiciliar. Para o exame de {secondDate}: 3 dias antes, parar com multivitamínico; 24h antes, não ir pro Muay Thai; jejum puro até a coleta.";

        var result = await CreateHandler().Handle(new(_userId, message,
            ClientContext: new(SupportsParts: true, SupportsPendingOperationChanges: true, Locale: language)), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.AiMessage.Should().BeNullOrEmpty();
        result.Value.Parts.Should().BeNullOrEmpty();
        var pending = result.Value.PendingOperations.Should().ContainSingle().Which;
        pending.Items.Should().HaveCount(5);
        var execution = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        var heldItems = execution.Arguments.GetProperty("habits");
        for (var index = 0; index < items.GetArrayLength(); index++)
            foreach (var field in items[index].EnumerateObject().Where(field =>
                field.Name is "title" or "due_date" or "end_date" or "due_time" or "due_end_time"
                    or "frequency_unit" or "frequency_quantity" or "is_bad_habit"))
            {
                var present = heldItems[index].TryGetProperty(field.Name, out var held);
                if (field.Value.ValueKind == JsonValueKind.Null)
                {
                    (present && held.ValueKind != JsonValueKind.Null).Should().BeFalse(field.Name);
                    pending.Items![index].Fields.Should().NotContain(value => value.Field == field.Name && value.NewValue != null);
                    continue;
                }
                present.Should().BeTrue(field.Name);
                JsonElement.DeepEquals(held, field.Value).Should().BeTrue(field.Name);
                var display = field.Value.ValueKind == JsonValueKind.False ? "False" : field.Value.ToString();
                pending.Items![index].Fields.Should().Contain(value => value.Field == field.Name && value.NewValue == display);
            }
        written.Should().BeEmpty();
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        AssertPreparationGuidance(_aiIntentService.ReceivedCalls().SelectMany(request => request.GetArguments())
            .OfType<AiToolRequest>().Single());

        var confirmation = _pendingOperationStore.Confirm(_userId, pending.Id)!;
        var executed = await _executor.ExecuteAsync(new(_userId, execution.OperationId, execution.Arguments,
            execution.Surface, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken));

        executed.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        executed.Operation.Payload.Should().BeOfType<BulkCreateResult>().Which.AppliedCount.Should().Be(5);
        written.Should().HaveCount(5).And.OnlyContain(item => !item.IsBadHabit);
        var multivitamins = written[2];
        multivitamins.FrequencyUnit.Should().Be(FrequencyUnit.Day);
        multivitamins.FrequencyQuantity.Should().Be(1);
        multivitamins.DueDate.Should().Be(eventDate.AddDays(-3));
        multivitamins.EndDate.Should().Be(eventDate);
        var restriction = written[3];
        restriction.FrequencyUnit.Should().Be(boundedRestriction ? FrequencyUnit.Day : null);
        restriction.FrequencyQuantity.Should().Be(boundedRestriction ? 1 : null);
        restriction.DueDate.Should().Be(eventDate.AddDays(-1));
        restriction.EndDate.Should().Be(boundedRestriction ? eventDate : null);
        var fasting = written[4];
        fasting.FrequencyUnit.Should().BeNull();
        fasting.DueDate.Should().Be(collectionDeadline ? eventDate : eventDate.AddDays(-1));
        fasting.DueTime.Should().BeNull();
        fasting.DueEndTime.Should().Be(collectionDeadline ? new TimeOnly(13, 30) : null);
        written.Take(2).Should().OnlyContain(item => item.FrequencyUnit == null && item.EndDate == null);
        written[0].DueDate.Should().Be(eventDate.AddDays(-4));
        written[0].DueTime.Should().Be(new TimeOnly(15, 50));
        written[1].DueDate.Should().Be(eventDate);
        written[1].DueTime.Should().Be(new TimeOnly(13, 30));
    }

    private static JsonElement ShiftMedicalDates(JsonElement arguments, int days, string language)
    {
        if (days == 0) return arguments.Clone();
        var node = JsonNode.Parse(arguments.GetRawText())!;
        var eventDate = DateOnly.Parse(arguments.GetProperty("habits")[1].GetProperty("due_date").GetString()!, CultureInfo.InvariantCulture);
        var format = language == "en" ? "M/d" : "d/M";
        foreach (var item in node["habits"]!.AsArray().OfType<JsonObject>())
        {
            foreach (var field in new[] { "due_date", "end_date" })
                if (item[field] is JsonValue value && value.TryGetValue<string>(out var text))
                    item[field] = DateOnly.Parse(text, CultureInfo.InvariantCulture).AddDays(days)
                        .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (item["description"] is JsonValue description && description.TryGetValue<string>(out var content))
                item["description"] = content.Replace(eventDate.ToString(format, CultureInfo.InvariantCulture),
                    eventDate.AddDays(days).ToString(format, CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }
        return JsonSerializer.SerializeToElement(node);
    }
}
