using System.Reflection;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Api.RateLimiting;
using Orbit.Application.Email.Commands;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Controllers;

public sealed class EmailControllerRateLimitTests
{
    [Fact]
    public void SesEvents_UsesDistributedRateLimit()
    {
        typeof(EmailController).GetMethod(nameof(EmailController.SesEvents))!
            .GetCustomAttribute<DistributedRateLimitAttribute>()
            .Should().NotBeNull();
    }

    [Fact]
    public async Task SesEvents_ReturnsRetryableStatusForCertificateDependencyFailure()
    {
        var sender = Substitute.For<ISender>();
        sender.Send(Arg.Any<ProcessSesEventCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new SnsCertificateFetchException(
                "SNS signing certificate is unavailable", new HttpRequestException())));
        var controller = CreateController(sender);

        var result = await controller.SesEvents(CancellationToken.None);

        result.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Theory]
    [InlineData(false, StatusCodes.Status400BadRequest)]
    [InlineData(true, StatusCodes.Status200OK)]
    public async Task SesEvents_KeepsInvalidAndValidResponses(bool verified, int expectedStatus)
    {
        var sender = Substitute.For<ISender>();
        sender.Send(Arg.Any<ProcessSesEventCommand>(), Arg.Any<CancellationToken>()).Returns(verified);
        var controller = CreateController(sender);

        var result = await controller.SesEvents(CancellationToken.None);

        result.Should().BeAssignableTo<StatusCodeResult>().Which.StatusCode.Should().Be(expectedStatus);
    }

    private static EmailController CreateController(ISender sender)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream("{}"u8.ToArray());
        return new EmailController(sender) { ControllerContext = new ControllerContext { HttpContext = context } };
    }
}
