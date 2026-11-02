using System.Security.Claims;
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
using Orbit.Domain.Interfaces;
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
        flags.GetEnabledKeysForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new[] { FeatureFlagKeys.AstraConversationRecording });
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetLatestConversationQuery>(), Arg.Any<CancellationToken>()).Returns(call =>
            new GetLatestConversationQueryHandler(fixture.Store, flags).Handle(call.Arg<GetLatestConversationQuery>(), default));
        mediator.Send(Arg.Any<GetConversationImageQuery>(), Arg.Any<CancellationToken>()).Returns(call =>
            new GetConversationImageQueryHandler(fixture.Store, flags).Handle(call.Arg<GetConversationImageQuery>(), default));
        var controller = Controller(mediator, fixture.User.Id);
        var latest = (await controller.GetLatestConversation(default)).Should().BeOfType<OkObjectResult>().Which.Value
            .Should().BeOfType<ConversationPage>().Subject;
        latest.Turns.Select(row => row.Role).Should().Equal("user", "assistant");
        var image = (await controller.GetConversationImage(fixture.ConversationId, turn.TurnId, default)).Should().BeOfType<FileContentResult>().Subject;
        image.FileContents.Should().Equal(ConversationTurnRecorderTests.Fixture.Image);
        controller.Response.Headers.CacheControl.ToString().Should().Be("private, no-store");
        var foreign = Controller(mediator, Guid.NewGuid());
        (await foreign.GetConversationImage(fixture.ConversationId, turn.TurnId, default)).Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(404);
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
