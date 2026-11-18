using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Queries;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;
using Microsoft.Extensions.Options;
using Orbit.Infrastructure.Tests.Services;

namespace Orbit.Infrastructure.Tests.Controllers;

public class ConversationEndpointTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeleteEndpoint_UsesSignedInAccountAndErasesOwnedRowsAndImages(bool owner)
    {
        using var fixture = new ConversationTurnRecorderTests.Fixture();
        await fixture.Store.RecordAsync(fixture.Capture(image: true));
        var user = owner ? fixture.User.Id : Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DeleteConversationCommand>(), Arg.Any<CancellationToken>()).Returns(call =>
            new DeleteConversationCommandHandler(fixture.Store).Handle(call.Arg<DeleteConversationCommand>(), default));
        var result = await Controller(mediator, user).DeleteConversation(fixture.ConversationId, default);
        if (owner)
        {
            result.Should().BeOfType<NoContentResult>();
            (await fixture.Db.ConversationTurns.CountAsync()).Should().Be(0);
            await fixture.Storage.Received(1).DeleteConversationObjectsAsync(fixture.User.Id, fixture.ConversationId, Arg.Any<CancellationToken>());
        }
        else
        {
            result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(404);
            (await fixture.Db.ConversationTurns.CountAsync()).Should().Be(2);
            await fixture.Storage.DidNotReceiveWithAnyArgs().DeleteConversationObjectsAsync(default);
        }
    }

    [Fact]
    public async Task LatestAndImageEndpoints_RestoreDisplayAndRejectAnotherAccount()
    {
        using var fixture = new ConversationTurnRecorderTests.Fixture();
        var turn = fixture.Capture(image: true);
        await fixture.Store.RecordAsync(turn);
        var flags = Substitute.For<IFeatureFlagService>();
        flags.GetEnabledKeysForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new[] { FeatureFlagKeys.AiConversationRecording });
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetLatestConversationQuery>(), Arg.Any<CancellationToken>()).Returns(call =>
            new GetLatestConversationQueryHandler(fixture.Store, flags, SameTitleDetailWriters.NoHabits()).Handle(call.Arg<GetLatestConversationQuery>(), default));
        mediator.Send(Arg.Any<GetConversationImageQuery>(), Arg.Any<CancellationToken>()).Returns(call =>
            new GetConversationImageQueryHandler(fixture.Store, flags).Handle(call.Arg<GetConversationImageQuery>(), default));
        var controller = Controller(mediator, fixture.User.Id);
        var latest = (await controller.GetLatestConversation(default)).Should().BeOfType<OkObjectResult>().Which.Value
            .Should().BeOfType<ConversationPage>().Subject;
        latest.Turns.Select(row => row.Role).Should().Equal("user", "assistant");
        var imageUrl = latest.Turns.Single(row => row.TurnId == turn.TurnId && row.Role == "user").Image!.Url;
        imageUrl.Should().Be($"/api/chat/conversations/{fixture.ConversationId}/images/{turn.TurnId}");
        var parts = imageUrl!.Split('/');
        var image = (await controller.GetConversationImage(Guid.Parse(parts[^3]), Guid.Parse(parts[^1]), default)).Should().BeOfType<FileStreamResult>().Subject;
        await using var imageStream = image.FileStream;
        using var bytes = new MemoryStream();
        await imageStream.CopyToAsync(bytes);
        bytes.ToArray().Should().Equal(ConversationTurnRecorderTests.Fixture.Image);
        controller.Response.Headers.CacheControl.ToString().Should().Be("private, no-store");
        var foreign = Controller(mediator, Guid.NewGuid());
        (await foreign.GetConversationImage(fixture.ConversationId, turn.TurnId, default)).Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(404);
    }

    /// <summary>
    /// A stored turn with two previews over two top-level "Water" habits: one is pending and one is approved. The
    /// page writes the detail on the pending one and leaves the approved one and the stored row as they are.
    /// </summary>
    [Theory]
    [InlineData(null, "Water (2 of 2)", null)]
    [InlineData("""{"locale":"pt-BR"}""", "Water (2º de 2)", null)]
    [InlineData("""{"locale":"en","supportsPendingOperationEntityDetail":true}""", "Water", "2 of 2")]
    public async Task LatestEndpoint_WritesTheSameTitleDetailOnAPendingPreviewOnly(string? clientContext, string name, string? detail)
    {
        using var fixture = new ConversationTurnRecorderTests.Fixture();
        var waters = Enumerable.Range(1, 2).Select(position => Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
            fixture.User.Id, "Water", FrequencyUnit.Day, 1, new DateOnly(2026, 4, 3), Position: position)).Value).ToArray();
        fixture.Db.Habits.AddRange(waters);
        await fixture.Db.SaveChangesAsync();
        var capture = fixture.Capture();
        var store = new PendingAgentOperationStore(fixture.Db, Options.Create(new AgentPlatformSettings()), fixture.Flags);
        var previews = waters.Select(water =>
        {
            var arguments = JsonSerializer.Serialize(new { habit_id = water.Id });
            var fingerprint = AgentOperationFingerprint.Compute("delete_habit", arguments);
            var proposal = store.Create(fixture.User.Id, new AgentCatalogService().GetCapabilityByChatTool("delete_habit")!,
                "delete_habit", arguments, "Delete Water", fingerprint, AgentExecutionSurface.Chat, new(fixture.ConversationId, capture.CreatedAtUtc));
            store.Revise(fixture.User.Id, proposal.Id, fingerprint, arguments, fingerprint, "preview").Should().BeTrue();
            return proposal with { Items = [new PendingOperationItem(water.Id.ToString(), water.Id, "Water", [], "state", RemovesData: true)] };
        }).ToArray();
        store.Confirm(fixture.User.Id, previews[0].Id).Should().NotBeNull();
        var responseJson = JsonSerializer.Serialize(new ChatResponse("Review the changes.", [], PendingOperations: previews),
            JsonSerializerOptions.Web);
        await fixture.Store.RecordAsync(capture with { ResponseJson = responseJson });
        var flags = Substitute.For<IFeatureFlagService>();
        flags.GetEnabledKeysForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new[] { FeatureFlagKeys.AiConversationRecording });
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetLatestConversationQuery>(), Arg.Any<CancellationToken>()).Returns(call =>
            new GetLatestConversationQueryHandler(fixture.Store, flags, SameTitleDetailWriters.Over(fixture.Db))
                .Handle(call.Arg<GetLatestConversationQuery>(), default));

        var result = await Controller(mediator, fixture.User.Id).GetLatestConversation(default, clientContext: clientContext);

        var turn = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<ConversationPage>().Subject
            .Turns.Single(row => row.Role == "assistant");
        JsonSerializer.Deserialize<ConversationPreviewOutcome[]>(turn.PreviewOutcomesJson)!.Select(outcome => outcome.Outcome)
            .Should().BeEquivalentTo("approved", "pending");
        var items = JsonSerializer.Deserialize<JsonElement>(turn.ResponseJson!).GetProperty("pendingOperations").EnumerateArray()
            .ToDictionary(preview => preview.GetProperty("id").GetGuid(), preview => preview.GetProperty("items")[0]);
        items[previews[0].Id].GetProperty("entityName").GetString().Should().Be("Water");
        items[previews[0].Id].TryGetProperty("entityDetail", out _).Should().BeFalse();
        items[previews[1].Id].GetProperty("entityName").GetString().Should().Be(name);
        (items[previews[1].Id].TryGetProperty("entityDetail", out var written) ? written.GetString() : null).Should().Be(detail);
        fixture.Db.ChangeTracker.Clear();
        var stored = await fixture.Db.ConversationTurns.AsNoTracking().SingleAsync(row => row.Role == "assistant");
        stored.ResponseJson.Should().NotContain("2 of 2").And.NotContain("de 2").And.NotContain("entityDetail");
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    public async Task LatestEndpoint_RefusesAClientContextThatIsNotAnObject(string clientContext)
    {
        var mediator = Substitute.For<IMediator>();

        var result = await Controller(mediator, Guid.NewGuid()).GetLatestConversation(default, clientContext: clientContext);

        result.Should().BeOfType<BadRequestObjectResult>();
        await mediator.DidNotReceiveWithAnyArgs().Send(default(GetLatestConversationQuery)!, default);
    }

    private static ChatController Controller(IMediator mediator, Guid user) => new(mediator, Substitute.For<IImageValidationService>(), NullLogger<ChatController>.Instance)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.ToString())], "Test"))
            }
        }
    };
}
