using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests
{
    private static readonly DateOnly FreshRestartToday = new(2026, 10, 30);

    [Fact]
    public void G12_RestartModelContractRequiresAnExplicitPlanKind()
    {
        var tool = _toolRegistry.GetTool("bulk_reschedule_habits")!;
        var schema = JsonSerializer.SerializeToElement(tool.GetParameterSchema());
        schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()).Should().Contain("restart_plan");
        schema.GetProperty("properties").GetProperty("restart_plan").GetProperty("type").GetString().Should().Be("boolean");
        schema.GetProperty("properties").GetProperty("restart_plan").GetProperty("description").GetString().Should().Be(
            "True for a restart plan covering every remaining due and overdue habit after a fresh query_habits in this turn. False for ordinary rescheduling.");
        tool.Description.Should().Contain(RestartPlanInstructions.FreshLookup).And.Contain(
            "Call bulk_reschedule_habits once with restart_plan=true and revised_items containing every habit in the complete set and its proposed due_date, including today's choices.");
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task G12_RestartCapturedFreshReadsFinishWithOnePreview(int repeat, bool incomplete)
    {
        var capture = FreshRestartCapture(repeat);
        var habits = await SetupFreshRestartAsync(capture);
        var before = await RestartReadbackAsync();
        var calls = FreshRestartCalls(capture, habits);
        if (incomplete)
            calls[^1] = WithRestartArgument(calls[^1], "orbit_write_complete", false);
        var rounds = 0;
        SetupFreshRestartModel(calls[0], results =>
        {
            ++rounds;
            var read = results.Should().ContainSingle().Which;
            read.Name.Should().Be("query_habits");
            read.Success.Should().BeTrue(read.Error);
            read.Payload.Should().BeOfType<HabitListCard>().Which.Items.Select(item => item.Id)
                .Should().BeEquivalentTo(habits.Select(habit => habit.Id.ToString()));
            return FreshRestartResponse(calls[^1]);
        });

        var result = await RunFreshRestartAsync();

        result.IsSuccess.Should().BeTrue(result.Error);
        rounds.Should().Be(1, "a validated restart is complete even when orbit_write_complete is false");
        AssertFreshRestartPreview(result.Value, habits);
        (await RestartReadbackAsync()).Should().BeEquivalentTo(before);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task G12_RestartCapturedSkippedReadIsRefusedThenCorrected(string language)
    {
        var capture = FreshRestartCapture(3);
        var habits = await SetupFreshRestartAsync(capture, language);
        var before = await RestartReadbackAsync();
        var write = FreshRestartCalls(capture, habits).Single();
        var read = FreshRestartCalls(FreshRestartCapture(1), habits)[0];
        var rounds = 0;
        SetupFreshRestartModel(write, results =>
        {
            ++rounds;
            var feedback = results.Should().ContainSingle().Which;
            if (rounds == 1)
            {
                feedback.Success.Should().BeFalse("the captured write omitted the required fresh query");
                feedback.Error.Should().Contain("query_habits").And.Contain("this turn");
                _pendingOperationStore.GetAwaiting(_userId).Should().BeEmpty();
                return FreshRestartResponse(read);
            }
            feedback.Name.Should().Be("query_habits");
            feedback.Success.Should().BeTrue(feedback.Error);
            return FreshRestartResponse(write with { Id = "corrected-restart" });
        });

        var result = await RunFreshRestartAsync(language);

        result.IsSuccess.Should().BeTrue(result.Error);
        rounds.Should().Be(2, "one refusal and one fresh read precede the corrected preview");
        AssertFreshRestartPreview(result.Value, habits);
        (await RestartReadbackAsync()).Should().BeEquivalentTo(before);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("all_today")]
    [InlineData("outside_week")]
    [InlineData("duplicate")]
    [InlineData("malformed")]
    [InlineData("other_field")]
    public async Task G12_RestartRejectsInvalidCurrentSetThenAcceptsCorrection(string defect)
    {
        var capture = FreshRestartCapture(1);
        var habits = await SetupFreshRestartAsync(capture);
        var calls = FreshRestartCalls(capture, habits);
        var arguments = JsonNode.Parse(calls[1].Args.GetRawText())!.AsObject();
        var items = arguments["revised_items"]!.AsArray();
        var extra = RestartHabit("Future appointment", FreshRestartToday.AddDays(10));
        _dbContext.Habits.Add(extra);
        await _dbContext.SaveChangesAsync();
        switch (defect)
        {
            case "missing": items.RemoveAt(5); break;
            case "extra":
                items.Add(JsonSerializer.SerializeToNode(new
                {
                    habit_id = extra.Id,
                    updates = new { due_date = "2026-10-31" }
                })); break;
            case "all_today":
                foreach (var item in items) item!["updates"]!["due_date"] = "2026-10-30";
                break;
            case "outside_week": items[5]!["updates"]!["due_date"] = "2026-11-07"; break;
            case "duplicate": items[5]!["habit_id"] = habits[0].Id.ToString(); break;
            case "malformed": items[5]!["updates"]!["due_date"] = "next week"; break;
            case "other_field": items[5]!["updates"]!["title"] = "Changed title"; break;
        }
        var rounds = 0;
        SetupFreshRestartModel(calls[0], results =>
        {
            ++rounds;
            if (rounds == 1)
                return FreshRestartResponse(calls[1] with { Args = JsonSerializer.SerializeToElement(arguments) });
            var feedback = results.Should().ContainSingle().Which;
            feedback.Success.Should().BeFalse("the restart must validate its entire current set and dates");
            feedback.Error.Should().Contain(defect switch
            {
                "missing" => habits[5].Title,
                "extra" => extra.Title,
                "all_today" => "fewer",
                "outside_week" => "seven days",
                _ => "revised_items"
            });
            _pendingOperationStore.GetAwaiting(_userId).Should().BeEmpty();
            return FreshRestartResponse(calls[1] with { Id = "corrected-set" });
        });

        var result = await RunFreshRestartAsync();

        result.IsSuccess.Should().BeTrue(result.Error);
        rounds.Should().Be(2);
        AssertFreshRestartPreview(result.Value, habits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task G12_OrdinaryRescheduleDoesNotRequireFreshRestartDataOrFinishEarly(bool individualDates)
    {
        var habits = await SetupFreshRestartAsync(FreshRestartCapture(1));
        var write = new AiToolCall("bulk_reschedule_habits", "ordinary-reschedule", JsonSerializer.SerializeToElement(new
        {
            restart_plan = false,
            filter = new { habit_ids = new[] { habits[0].Id } },
            due_date = "2026-11-20",
            orbit_write_complete = false
        }));
        if (individualDates)
            write = write with
            {
                Args = JsonSerializer.SerializeToElement(new
                {
                    restart_plan = false,
                    revised_items = new[] { new { habit_id = habits[0].Id, updates = new { due_date = "2026-11-20" } } },
                    orbit_write_complete = false
                })
            };
        var rounds = 0;
        SetupFreshRestartModel(write, results =>
        {
            ++rounds;
            results.Should().ContainSingle().Which.Success.Should().BeTrue();
            return new AiResponse();
        });

        var result = await RunFreshRestartAsync();

        result.IsSuccess.Should().BeTrue(result.Error);
        rounds.Should().Be(1, "ordinary rescheduling still honors orbit_write_complete");
        var preview = result.Value.PendingOperations.Should().ContainSingle().Which;
        preview.Items.Should().ContainSingle().Which.EntityId.Should().Be(habits[0].Id);
        preview.Items![0].Fields.Should().ContainSingle().Which.NewValue.Should().Be("2026-11-20");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task G12_RestartRequiresEveryFreshQueryPage(bool tryBeforeLastPage)
    {
        var capture = FreshRestartCapture(1);
        var habits = await SetupFreshRestartAsync(capture);
        var calls = FreshRestartCalls(capture, habits);
        AiToolCall Page(int offset)
        {
            var arguments = JsonNode.Parse(calls[0].Args.GetRawText())!.AsObject();
            arguments["limit"] = 2;
            arguments["offset"] = offset;
            return calls[0] with { Id = "page-" + offset, Args = JsonSerializer.SerializeToElement(arguments) };
        }
        var final = WithRestartArgument(calls[1], "orbit_write_complete", false) with { Id = "paged-restart" };
        AiToolCall[] plan = tryBeforeLastPage
            ? [Page(0), calls[1], Page(2), Page(4), final]
            : [Page(0), Page(2), Page(4), final];
        var rounds = 0;
        SetupFreshRestartModel(plan[0], results =>
        {
            var feedback = results.Should().ContainSingle().Which;
            var previous = plan[rounds++];
            if (previous.Name == "query_habits")
            {
                feedback.Success.Should().BeTrue(feedback.Error);
                var page = feedback.Payload.Should().BeOfType<HabitListCard>().Which;
                page.TotalCount.Should().Be(6);
                page.Items.Should().HaveCount(2);
            }
            else
            {
                feedback.Success.Should().BeFalse();
                feedback.Error.Should().Contain("every page");
            }
            _pendingOperationStore.GetAwaiting(_userId).Should().BeEmpty();
            return FreshRestartResponse(plan[rounds]);
        });

        var result = await RunFreshRestartAsync();

        result.IsSuccess.Should().BeTrue(result.Error);
        rounds.Should().Be(plan.Length - 1);
        AssertFreshRestartPreview(result.Value, habits);
    }

    [Fact]
    public async Task G12_RestartDetectsMembershipChangesAfterTheRead()
    {
        var capture = FreshRestartCapture(1);
        var habits = await SetupFreshRestartAsync(capture);
        var calls = FreshRestartCalls(capture, habits);
        var added = RestartHabit("New due habit", FreshRestartToday);
        var corrected = JsonNode.Parse(calls[1].Args.GetRawText())!.AsObject();
        corrected["revised_items"]!.AsArray().Add(JsonSerializer.SerializeToNode(new
        {
            habit_id = added.Id,
            updates = new { due_date = "2026-10-31" }
        }));
        var rounds = 0;
        SetupFreshRestartModel(calls[0], results =>
        {
            ++rounds;
            if (rounds == 1)
            {
                _dbContext.Habits.Add(added);
                _dbContext.SaveChanges();
                return FreshRestartResponse(calls[1]);
            }
            if (rounds == 2)
            {
                var feedback = results.Should().ContainSingle().Which;
                feedback.Success.Should().BeFalse();
                feedback.Error.Should().Contain("query_habits").And.Contain(added.Title);
                _pendingOperationStore.GetAwaiting(_userId).Should().BeEmpty();
                return FreshRestartResponse(calls[0] with { Id = "refreshed-set" });
            }
            results.Should().ContainSingle().Which.Success.Should().BeTrue();
            return FreshRestartResponse(calls[1] with { Id = "current-set", Args = JsonSerializer.SerializeToElement(corrected) });
        });

        var result = await RunFreshRestartAsync();

        result.IsSuccess.Should().BeTrue(result.Error);
        rounds.Should().Be(3);
        AssertFreshRestartPreview(result.Value, habits.Append(added).ToArray());
    }

    private static JsonElement FreshRestartCapture(int repeat)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "RestartPlans", $"prod-r2-composition-restart-approve-pt-BR-r{repeat}.json")));
        return document.RootElement.Clone();
    }

    private async Task<Habit[]> SetupFreshRestartAsync(JsonElement capture, string language = "pt-BR")
    {
        _user.SetLanguage(language);
        _userDateService.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(FreshRestartToday);
        var habits = capture.GetProperty("previewItems").EnumerateArray().Select(item => RestartHabit(
            item.GetProperty("entityName").GetString()!, DateOnly.ParseExact(
                item.GetProperty("fields")[0].GetProperty("oldValue").GetString()!, "yyyy-MM-dd",
                CultureInfo.InvariantCulture))).ToArray();
        var done = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, _userId,
            "Already completed", null, null, FreshRestartToday)).Value;
        done.Log(FreshRestartToday).IsSuccess.Should().BeTrue();
        done.IsCompleted.Should().BeTrue();
        _dbContext.Habits.AddRange(habits.Append(done));
        await _dbContext.SaveChangesAsync();
        SetupRestartRepository();
        return habits;
    }

    private static AiToolCall[] FreshRestartCalls(JsonElement capture, IReadOnlyList<Habit> habits) =>
        capture.GetProperty("toolCalls").EnumerateArray().Select((call, index) =>
        {
            var arguments = JsonNode.Parse(call.GetProperty("arguments").GetRawText())!.AsObject();
            if (arguments["revised_items"] is JsonArray items)
            {
                arguments["restart_plan"] = true;
                for (var item = 0; item < items.Count; item++)
                    items[item]!["habit_id"] = habits[item].Id.ToString();
            }
            return new AiToolCall(call.GetProperty("name").GetString()!, "fresh-restart-" + index,
                JsonSerializer.SerializeToElement(arguments));
        }).ToArray();

    private static AiToolCall WithRestartArgument(AiToolCall call, string name, bool value)
    {
        var arguments = JsonNode.Parse(call.Args.GetRawText())!.AsObject();
        arguments[name] = value;
        return call with { Args = JsonSerializer.SerializeToElement(arguments) };
    }

    private static AiResponse FreshRestartResponse(AiToolCall call) => new()
    {
        ToolCalls = [call],
        ConversationContext = new()
    };

    private void SetupFreshRestartModel(AiToolCall first, Func<IReadOnlyList<AiToolCallResult>, AiResponse> next)
    {
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(),
            Arg.Any<CancellationToken>()).Returns(Result.Success(FreshRestartResponse(first)));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(call => Result.Success(next(call.Arg<IReadOnlyList<AiToolCallResult>>())));
    }

    private Task<Result<ChatResponse>> RunFreshRestartAsync(string language = "pt-BR") => CreateHandler().Handle(
        new(_userId, language == "en" ? "Help me restart." : "Me ajude a recomeçar.", ClientContext: new(
            SupportsParts: true, SupportsPendingOperationChanges: true, SupportsHabitListCard: true, Locale: language)), default);

    private static void AssertFreshRestartPreview(ChatResponse response, IReadOnlyList<Habit> habits)
    {
        response.AiMessage.Should().BeNullOrEmpty();
        response.Parts.Should().BeNullOrEmpty();
        var preview = response.PendingOperations.Should().ContainSingle().Which;
        preview.Items!.Select(item => item.EntityId).Should().BeEquivalentTo(habits.Select(habit => (Guid?)habit.Id));
        preview.Items!.Select(item => item.EntityName).Should().BeEquivalentTo(habits.Select(habit => habit.Title));
    }
}
