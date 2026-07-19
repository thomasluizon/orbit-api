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

    [Theory]
    [InlineData(ErrorCodes.GoalNotFound, 404)]
    [InlineData(ErrorCodes.HabitNotFound, 404)]
    [InlineData(ErrorCodes.TagNotFound, 404)]
    [InlineData(ErrorCodes.NoActiveSubscription, 404)]
    [InlineData(ErrorCodes.NoPermission, 403)]
    [InlineData(ErrorCodes.HabitNotOwned, 403)]
    [InlineData(Result.PayGateErrorCode, 403)]
    [InlineData(ErrorCodes.DuplicateTagName, 409)]
    [InlineData(ErrorCodes.DuplicateFact, 409)]
    [InlineData(ErrorCodes.InternalServerError, 500)]
    [InlineData(ErrorCodes.ValidationError, 400)]
    [InlineData(ErrorCodes.DeadlineInPast, 400)]
    [InlineData(ErrorCodes.PaymentServiceUnavailable, 503)]
    [InlineData(ErrorCodes.BillingDetailsUnavailable, 503)]
    public void ToErrorResult_MapsErrorCodeToStatus(string errorCode, int expectedStatus)
    {
        var result = Result.Failure("failure", errorCode);

        var actionResult = result.ToErrorResult();

        actionResult.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(expectedStatus);
    }

    [Fact]
    public void ToErrorResult_UnmappedCode_FallsBackToProvidedStatus()
    {
        var result = Result.Failure("login failed", ErrorCodes.InvalidVerificationCode);

        var actionResult = result.ToErrorResult(StatusCodes.Status401Unauthorized);

        actionResult.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(401);
    }

    [Fact]
    public void ToErrorResult_NoErrorCode_FallsBackToProvidedStatus()
    {
        var result = Result.Failure("something went wrong");

        var actionResult = result.ToErrorResult(StatusCodes.Status500InternalServerError);

        actionResult.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(500);
    }

    [Fact]
    public void ResolveErrorStatus_MappedCode_IgnoresFallback()
    {
        var result = Result.Failure("missing", ErrorCodes.GoalNotFound);

        result.ResolveErrorStatus(StatusCodes.Status400BadRequest).Should().Be(404);
    }
}
