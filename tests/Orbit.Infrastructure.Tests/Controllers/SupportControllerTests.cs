using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Support.Commands;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Controllers;

public class SupportControllerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly ILogger<SupportController> _logger = Substitute.For<ILogger<SupportController>>();
    private readonly SupportController _controller;
    private static readonly Guid UserId = Guid.NewGuid();

    public SupportControllerTests()
    {
        _controller = new SupportController(_mediator, _logger);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    [Fact]
    public async Task SendSupport_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<SendSupportCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var request = new SupportController.SupportRequest("John", "john@example.com", "Bug Report", "Something broke");
        var result = await _controller.SendSupport(request, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task SendSupport_LoggingLevel_PreservesResponseAndSkipsUnusedArguments(bool enabled, int expectedUserReads)
    {
        var principal = new CountingPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, UserId.ToString())], "Test"));
        _controller.HttpContext.User = principal;
        _logger.IsEnabled(LogLevel.Information).Returns(enabled);
        _mediator.Send(Arg.Any<SendSupportCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var request = new SupportController.SupportRequest("John", "john@example.com", "Bug Report", "Something broke");
        var response = await _controller.SendSupport(request, CancellationToken.None);

        var result = response.Should().BeOfType<OkObjectResult>().Subject;
        System.Text.Json.JsonSerializer.Serialize(result.Value)
            .Should().Be("""{"message":"Support request sent successfully"}""");
        principal.Reads.Should().Be(expectedUserReads);
        await _mediator.Received(1).Send(
            Arg.Is<SendSupportCommand>(command => command.UserId == UserId), CancellationToken.None);
    }

    private sealed class CountingPrincipal(ClaimsIdentity identity) : ClaimsPrincipal(identity)
    {
        public int Reads { get; private set; }

        public override Claim? FindFirst(string type)
        {
            Reads++;
            return base.FindFirst(type);
        }
    }

    [Fact]
    public async Task SendSupport_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<SendSupportCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Failed to send"));

        var request = new SupportController.SupportRequest("John", "john@example.com", "Bug Report", "Something broke");
        var result = await _controller.SendSupport(request, CancellationToken.None);

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(400);
    }
}
