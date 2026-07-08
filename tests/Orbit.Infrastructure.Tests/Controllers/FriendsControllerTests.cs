using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Common;
using Orbit.Application.Social.Commands;
using Orbit.Application.Social.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Enums;

namespace Orbit.Infrastructure.Tests.Controllers;

public class FriendsControllerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly ILogger<FriendsController> _logger = Substitute.For<ILogger<FriendsController>>();
    private readonly FriendsController _controller;
    private static readonly Guid UserId = Guid.NewGuid();

    public FriendsControllerTests()
    {
        _controller = new FriendsController(_mediator, _logger);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    [Fact]
    public async Task GetFriends_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetFriendsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<FriendsResponse>(default!));

        var result = await _controller.GetFriends(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetFriends_PayGateFailure_Returns403()
    {
        _mediator.Send(Arg.Any<GetFriendsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<FriendsResponse>("Pro required"));

        var result = await _controller.GetFriends(CancellationToken.None);

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task GetFriendProfile_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetFriendProfileQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<FriendProfileView>(default!));

        var result = await _controller.GetFriendProfile(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetInvitePreview_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetInvitePreviewQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<InvitePreviewView>(default!));

        var result = await _controller.GetInvitePreview("code", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetFeed_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetFriendFeedQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<FriendFeedPage>(default!));

        var result = await _controller.GetFeed(null, null, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetCheers_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetCheersQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<CheersPage>(default!));

        var result = await _controller.GetCheers(cancellationToken: CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task SendRequest_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<SendFriendRequestCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(Guid.NewGuid()));

        var body = new FriendsController.SendFriendRequestBody("handle", null);
        var result = await _controller.SendRequest(body, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task SendRequest_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<SendFriendRequestCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<Guid>("Handle not found"));

        var body = new FriendsController.SendFriendRequestBody("unknown", null);
        var result = await _controller.SendRequest(body, CancellationToken.None);

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task AcceptRequest_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<AcceptFriendRequestCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _controller.AcceptRequest(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task RemoveFriend_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<RemoveFriendCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _controller.RemoveFriend(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task SendCheer_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<SendCheerCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(Guid.NewGuid()));

        var body = new FriendsController.SendCheerBody(Guid.NewGuid(), null, "Nice work!");
        var result = await _controller.SendCheer(body, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task SendCheer_SocialDisabled_Returns403()
    {
        _mediator.Send(Arg.Any<SendCheerCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<Guid>("Social disabled", ErrorCodes.SocialDisabled));

        var body = new FriendsController.SendCheerBody(Guid.NewGuid(), null, null);
        var result = await _controller.SendCheer(body, CancellationToken.None);

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task Block_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<BlockUserCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var body = new FriendsController.BlockUserBody(Guid.NewGuid());
        var result = await _controller.Block(body, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task Unblock_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<UnblockUserCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _controller.Unblock(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task Report_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<ReportUserCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(Guid.NewGuid()));

        var body = new FriendsController.ReportUserBody(Guid.NewGuid(), ReportReason.Spam, null, null);
        var result = await _controller.Report(body, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }
}
