using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Services;

public sealed partial class FirstTryPreviewTests
{
    public static TheoryData<string, bool> ContextFreeRoutes()
    {
        var data = new TheoryData<string, bool>();
        foreach (var route in new[] { "suggestion-create", "preview-all-one", "picklist-create", "resolve-create",
                     "suggestion-update", "preview-all-set" })
        {
            data.Add(route, false);
            data.Add(route, true);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(ContextFreeRoutes))]
    public async Task ContextFreeBind_ApprovesAndRevisesFromAClientWithOffersOnTheFirstTry(string route, bool revise)
    {
        var existing = AddHabit("Read");
        var preview = await BindWithoutClientContextAsync(route, existing.Id);
        preview.Items.Should().NotBeNullOrEmpty();
        var rows = PendingRows();

        if (revise)
        {
            var item = preview.Items![0];
            var edited = await _revisions.ReviseAsync(_userId, preview.Id, new RevisePendingOperationRequest(
                preview.PreviewFingerprint!, [new RevisedPendingOperationItem(item.ItemId,
                    JsonSerializer.SerializeToElement(new { title = "Read more" }))], OfferClient()), default);

            edited.IsSuccess.Should().BeTrue(edited.Error);
            PendingRows().Should().Be(rows);
            return;
        }

        var result = await ApproveAsync(preview.Id, OfferClient());

        Succeeded(result);
        PendingRows().Should().Be(rows, "an approval never proposes the change again");
        var saved = await _factory.Context.Habits.AsNoTracking().Select(habit => habit.Title).ToListAsync();
        var updated = await _factory.Context.Habits.AsNoTracking().SingleAsync(habit => habit.Id == existing.Id);
        if (route is "suggestion-update" or "preview-all-set")
            updated.DueTime.Should().Be(new TimeOnly(6, 0));
        if (route is "suggestion-update")
            saved.Should().ContainSingle();
        else
            saved.Should().HaveCount(2).And.Contain(title => title.StartsWith("Journal"));
    }

    private async Task<PendingAgentOperation> BindWithoutClientContextAsync(string route, Guid existing)
    {
        switch (route)
        {
            case "suggestion-create":
            case "suggestion-update":
                {
                    var draft = route == "suggestion-create" ? Draft("Journal") : Draft("Read", existing, new TimeOnly(6, 0));
                    var result = await new PreviewHabitSuggestionCommandHandler(_suggestions, _executor, _sameTitleDetail)
                        .Handle(new(_userId, draft, "en"), default);
                    result.IsSuccess.Should().BeTrue(result.Error);
                    return Pending(result.Value);
                }
            case "preview-all-one":
            case "preview-all-set":
                {
                    IReadOnlyList<HabitSuggestionDraft> drafts = route == "preview-all-one"
                        ? [Draft("Journal")] : [Draft("Read", existing, new TimeOnly(6, 0)), Draft("Journal")];
                    var result = await new PreviewAllHabitSuggestionsCommandHandler(_suggestions, _executor, _changeSets, _store, _sameTitleDetail)
                        .Handle(new(_userId, drafts, "en"), default);
                    result.IsSuccess.Should().BeTrue(result.Error);
                    return Pending(result.Value);
                }
            case "picklist-create":
                {
                    var item = new AiBlockItem("draft", "Journal", JsonSerializer.SerializeToElement(new
                    {
                        title = "Journal",
                        frequency_unit = "Day",
                        frequency_quantity = 1
                    }));
                    return Pending(await _executor.ExecuteAsync(new AgentExecuteOperationRequest(_userId, "bulk_create_habits",
                        AskTool.BuildBulkArguments("create_habits", [item]), AgentExecutionSurface.Chat, AgentAuthMethod.Jwt,
                        IncludeChangePreview: true, PreviewOnly: true)));
                }
            case "resolve-create":
                {
                    var stash = await _clarifications.CreateAsync(_userId, "create_habit",
                        JsonSerializer.Serialize(new { title = "Journal habit" }), "frequency_unit", "How often?",
                        JsonSerializer.Serialize(new[] { new QuickAction("daily", """{"frequency_unit":"Day","frequency_quantity":1}""") }));
                    var result = await _controller.ResolveClarification(stash,
                        new ResolveClarificationRequest("""{"frequency_unit":"Day","frequency_quantity":1}"""), default);
                    return Pending(result.Should().BeOfType<OkObjectResult>().Which.Value.Should()
                        .BeAssignableTo<AgentExecuteOperationResponse>().Subject);
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(route), route, null);
        }
    }

    private static PendingAgentOperation Pending(AgentExecuteOperationResponse response)
    {
        response.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation, JsonSerializer.Serialize(response));
        return response.PendingOperation!;
    }
}
