using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Marketing.Commands;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Controllers;

public class MarketingControllerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly MarketingController _controller;

    public MarketingControllerTests()
    {
        _controller = new MarketingController(_mediator);
    }

    [Fact]
    public async Task Unsubscribe_Success_ReturnsHtmlContent()
    {
        _mediator.Send(Arg.Any<UnsubscribeMarketingCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _controller.Unsubscribe("token", null, CancellationToken.None);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.ContentType.Should().Be("text/html");
    }

    [Fact]
    public async Task Unsubscribe_SuccessPortuguese_ReturnsHtmlContent()
    {
        _mediator.Send(Arg.Any<UnsubscribeMarketingCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _controller.Unsubscribe("token", "pt", CancellationToken.None);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.ContentType.Should().Be("text/html");
    }

    [Fact]
    public async Task Unsubscribe_Failure_ReturnsBadRequestHtml()
    {
        _mediator.Send(Arg.Any<UnsubscribeMarketingCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Invalid token"));

        var result = await _controller.Unsubscribe("bad", null, CancellationToken.None);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task UnsubscribeOneClick_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<UnsubscribeMarketingCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _controller.UnsubscribeOneClick("token", CancellationToken.None);

        result.Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task UnsubscribeOneClick_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<UnsubscribeMarketingCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Invalid token"));

        var result = await _controller.UnsubscribeOneClick("bad", CancellationToken.None);

        result.Should().BeOfType<BadRequestResult>();
    }
}
