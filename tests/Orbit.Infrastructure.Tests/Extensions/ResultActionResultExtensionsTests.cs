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
        // Arrange
        var result = Result.Success();

        // Act
        var actionResult = result.ToPayGateAwareResult();

        // Assert
        actionResult.Should().BeOfType<OkResult>();
    }

    [Fact]
    public void ToPayGateAwareResult_Failure_ReturnsBadRequest()
    {
        // Arrange
        var result = Result.Failure("Something went wrong");

        // Act
        var actionResult = result.ToPayGateAwareResult();

        // Assert
        actionResult.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void ToPayGateAwareResult_PayGate_Returns403()
    {
        // Arrange
        var result = Result.PayGateFailure("Upgrade required");

        // Act
        var actionResult = result.ToPayGateAwareResult();

        // Assert
        var objectResult = actionResult.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(403);
    }

    [Fact]
    public void ToPayGateAwareResultT_Success_CallsOnSuccess()
    {
        // Arrange
        var result = Result.Success<string>("hello");
        var called = false;

        // Act
        var actionResult = result.ToPayGateAwareResult(value =>
        {
            called = true;
            return new OkObjectResult(value);
        });

        // Assert
        called.Should().BeTrue();
        var okResult = actionResult.Should().BeOfType<OkObjectResult>().Subject;
        okResult.Value.Should().Be("hello");
    }
}
