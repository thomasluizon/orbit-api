using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Orbit.Api.Extensions;
using Orbit.Api.Middleware;
using Orbit.Application.Common;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Middleware;

/// <summary>
/// The filter is the one seam that keeps a domain guard's developer-facing sentence off the
/// wire, so these tests drive it through the real result pipeline rather than calling the
/// catalog directly.
/// </summary>
public class LocalizedErrorResultFilterTests
{
    private static ErrorResponse Run(IActionResult result, string? acceptLanguage)
    {
        var httpContext = new DefaultHttpContext();
        if (acceptLanguage is not null)
            httpContext.Request.Headers.AcceptLanguage = acceptLanguage;

        var context = new ResultExecutingContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()),
            [],
            result,
            controller: null!);

        new LocalizedErrorResultFilter().OnResultExecuting(context);

        var objectResult = context.Result.Should().BeOfType<ObjectResult>().Subject;
        return objectResult.Value.Should().BeOfType<ErrorResponse>().Subject;
    }

    [Fact]
    public void EnglishRequest_GetsTheEnglishCopy()
    {
        var body = Run(Result.Failure(ErrorMessages.HabitNotFound).ToErrorResult(), "en-US,en;q=0.9");

        body.Error.Should().Be(ErrorCopy.All[ErrorCodes.HabitNotFound].En);
        body.ErrorCode.Should().Be(ErrorCodes.HabitNotFound);
    }

    [Fact]
    public void PortugueseRequest_GetsThePortugueseCopy()
    {
        var body = Run(Result.Failure(ErrorMessages.HabitNotFound).ToErrorResult(), "pt-BR,pt;q=0.9,en;q=0.8");

        body.Error.Should().Be(ErrorCopy.All[ErrorCodes.HabitNotFound].PtBr);
        body.ErrorCode.Should().Be(ErrorCodes.HabitNotFound);
    }

    [Fact]
    public void MissingAcceptLanguage_FallsBackToEnglish()
    {
        var body = Run(Result.Failure(ErrorMessages.HabitNotFound).ToErrorResult(), acceptLanguage: null);

        body.Error.Should().Be(ErrorCopy.All[ErrorCodes.HabitNotFound].En);
    }

    [Fact]
    public void ADomainGuardSentenceNeverReachesTheWire()
    {
        var guard = DomainErrors.DaysRequireQuantityOne;

        var english = Run(Result.Failure(guard).ToErrorResult(), "en");
        var portuguese = Run(Result.Failure(guard).ToErrorResult(), "pt-BR");

        english.Error.Should().NotBe(guard.Message);
        portuguese.Error.Should().NotBe(guard.Message);
        english.ErrorCode.Should().Be(guard.Code);
        portuguese.ErrorCode.Should().Be(guard.Code);
    }

    [Fact]
    public void APlaceholderIsFilledFromTheResultArguments()
    {
        var body = Run(Result.Failure(ErrorMessages.MaxTagsPerHabit.Format(4)).ToErrorResult(), "pt-BR");

        body.Error.Should().Contain("4").And.NotContain("{0}");
    }

    [Fact]
    public void TheStatusCodeIsUntouched()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.AcceptLanguage = "pt-BR";
        var result = Result.Failure(ErrorMessages.HabitNotFound).ToErrorResult(StatusCodes.Status404NotFound);

        var context = new ResultExecutingContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()),
            [],
            result,
            controller: null!);

        new LocalizedErrorResultFilter().OnResultExecuting(context);

        context.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public void ANonErrorResultPassesThroughUntouched()
    {
        var httpContext = new DefaultHttpContext();
        var original = new OkObjectResult(new { value = 1 });
        var context = new ResultExecutingContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()),
            [],
            original,
            controller: null!);

        new LocalizedErrorResultFilter().OnResultExecuting(context);

        context.Result.Should().BeSameAs(original);
    }
}
