using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Common;
using Orbit.Application.Proactive.Commands;
using Orbit.Application.Proactive.Queries;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Controllers;

public sealed class ProactiveAstraControllerTests
{
    private readonly ISender _sender = Substitute.For<ISender>();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly ProactiveAstraController _controller;

    public ProactiveAstraControllerTests()
    {
        _controller = new(_sender)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, _userId.ToString())], "Test"))
                }
            }
        };
    }

    [Fact]
    public async Task Current_EmptyIs204AndUsesAuthenticatedOwner()
    {
        _sender.Send(Arg.Any<GetCurrentProactiveQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<ProactiveItemResponse?>(null));
        (await _controller.Current(default)).Should().BeOfType<NoContentResult>();
        await _sender.Received(1).Send(Arg.Is<GetCurrentProactiveQuery>(q => q.UserId == _userId), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Outcome_MissingItemIs404AndUsesAuthenticatedOwner(bool acted)
    {
        var id = Guid.NewGuid();
        _sender.Send(Arg.Any<RecordProactiveOutcomeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure(ErrorMessages.ProactiveItemNotFound));
        var result = acted ? await _controller.Acted(id, default) : await _controller.Opened(id, default);
        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(404);
        await _sender.Received(1).Send(Arg.Is<RecordProactiveOutcomeCommand>(q => q.UserId == _userId && q.ItemId == id && q.Acted == acted), Arg.Any<CancellationToken>());
    }
}
