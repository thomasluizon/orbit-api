using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Extensions;

public class ResultActionResultExtensionsTests
{
    [Fact]
    public void ToPayGateAwareResult_Success_ReturnsOkResult()
    {
        var result = Result.Success();

        var actionResult = result.ToPayGateAwareResult();

        actionResult.Should().BeOfType<OkResult>();
    }

    [Fact]
    public void ToPayGateAwareResult_Failure_ReturnsBadRequest()
    {
        var result = Result.Failure("Something went wrong");

        var actionResult = result.ToPayGateAwareResult();

        actionResult.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void ToPayGateAwareResult_PayGate_Returns403()
    {
        var result = Result.PayGateFailure("Upgrade required");

        var actionResult = result.ToPayGateAwareResult();

        var objectResult = actionResult.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(403);
    }

    [Fact]
    public void ToPayGateAwareResultT_Success_CallsOnSuccess()
    {
        var result = Result.Success<string>("hello");
        var called = false;

        var actionResult = result.ToPayGateAwareResult(value =>
        {
            called = true;
            return new OkObjectResult(value);
        });

        called.Should().BeTrue();
        var okResult = actionResult.Should().BeOfType<OkObjectResult>().Subject;
        okResult.Value.Should().Be("hello");
    }
}
