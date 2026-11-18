using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Chat.Models;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Services;

public sealed partial class FirstTryPreviewTests
{
    [Fact]
    public async Task ResolveOfStashedOfferArguments_BindsWithTheStashedContextAndApproves()
    {
        var parent = AddHabit("Morning routine");
        var stash = await _clarifications.CreateAsync(_userId, "create_habit",
            JsonSerializer.Serialize(new { title = "Stretch habit", parent_habit_id = parent.Id, suggested_fields = new { emoji = "🧘" } }),
            "frequency_unit", "How often?",
            JsonSerializer.Serialize(new[] { new QuickAction("daily", """{"frequency_unit":"Day","frequency_quantity":1}""") }),
            clientContext: OfferClient());

        var resolved = await _controller.ResolveClarification(stash,
            new ResolveClarificationRequest("""{"frequency_unit":"Day","frequency_quantity":1}"""), default);

        var preview = Pending(resolved.Should().BeOfType<OkObjectResult>().Which.Value.Should()
            .BeAssignableTo<AgentExecuteOperationResponse>().Subject);
        var item = preview.Items!.Single();
        item.Placement.Should().NotBeNull("a create under a parent shows the placement choice");
        item.Fields.Should().Contain(field => field.Field == "emoji" && field.Provenance == "suggested");
        (await _factory.Context.PendingAgentOperations.AsNoTracking().SingleAsync(row => row.Id == preview.Id))
            .ConversationId.Should().Be(_conversationId);
        var rows = PendingRows();
        Succeeded(await ApproveAsync(preview.Id, OfferClient()));
        PendingRows().Should().Be(rows);
        var saved = await _factory.Context.Habits.AsNoTracking().SingleAsync(habit => habit.Title == "Stretch habit");
        saved.ParentHabitId.Should().Be(parent.Id);
        saved.Emoji.Should().Be("🧘");
    }
}
