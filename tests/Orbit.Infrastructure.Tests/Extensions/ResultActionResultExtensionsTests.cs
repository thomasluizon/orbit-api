using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Application.Common;
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
    public void ToErrorResult_Failure_EmitsErrorAndErrorCode()
    {
        var result = Result.Failure(ErrorMessages.HabitNotFound);

        var actionResult = result.ToErrorResult(StatusCodes.Status404NotFound);

        var objectResult = actionResult.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(404);
        objectResult.Value.Should().BeEquivalentTo(new
        {
            error = ErrorMessages.HabitNotFound.Message,
            errorCode = ErrorMessages.HabitNotFound.Code
        });
    }

    [Fact]
    public void ToErrorResult_PayGateFailure_Emits403AndPayGateCode()
    {
        var result = Result.PayGateFailure("Upgrade required");

        var actionResult = result.ToErrorResult();

        var objectResult = actionResult.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(403);
        objectResult.Value.Should().BeEquivalentTo(new
        {
            error = "Upgrade required",
            errorCode = Result.PayGateErrorCode
        });
    }

    [Fact]
    public void ToErrorBody_AppError_EmitsErrorAndErrorCode()
    {
        var body = ErrorMessages.InvalidChatHistory.ToErrorBody();

        body.Should().BeEquivalentTo(new
        {
            error = ErrorMessages.InvalidChatHistory.Message,
            errorCode = ErrorMessages.InvalidChatHistory.Code
        });
    }

    [Fact]
    public void ToPayGateAwareResult_Failure_ReturnsBadRequest()
    {
        var result = Result.Failure("Something went wrong");

        var actionResult = result.ToPayGateAwareResult();

        actionResult.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(400);
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
