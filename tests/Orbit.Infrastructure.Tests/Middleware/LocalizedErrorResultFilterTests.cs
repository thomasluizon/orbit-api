using System.Security.Claims;
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
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using NSubstitute;

namespace Orbit.Infrastructure.Tests.Middleware;

/// <summary>
/// The filter is the one seam that keeps a domain guard's developer-facing sentence off the
/// wire, so these tests drive it through the real result pipeline rather than calling the
/// catalog directly. The language side is driven through the real
/// <see cref="RequestLanguageResolver"/> over a substituted repository, because the defect these
/// cover was the resolver reading the wrong input rather than the filter misapplying it.
/// </summary>
public class LocalizedErrorResultFilterTests
{
    private static ResultExecutingContext ContextFor(
        IActionResult result, string? acceptLanguage, ClaimsPrincipal? signedInAs = null)
    {
        var httpContext = new DefaultHttpContext();
        if (acceptLanguage is not null)
            httpContext.Request.Headers.AcceptLanguage = acceptLanguage;
        if (signedInAs is not null)
            httpContext.User = signedInAs;

        return new ResultExecutingContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()),
            [],
            result,
            controller: null!);
    }

    private static async Task<ResultExecutingContext> RunContext(
        IActionResult result,
        string? acceptLanguage,
        Guid? userId = null,
        string? storedLanguage = null)
    {
        var principal = userId is null
            ? null
            : new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString())],
                authenticationType: "Test"));

        var repository = Substitute.For<IGenericRepository<User>>();
        if (userId is not null)
        {
            var user = User.Create("Reader", $"{userId}@orbit.test").Value;
            user.SetLanguage(storedLanguage);
            repository.GetByIdAsync(userId.Value, Arg.Any<CancellationToken>()).Returns(user);
        }

        var context = ContextFor(result, acceptLanguage, principal);

        await new LocalizedErrorResultFilter(new RequestLanguageResolver(repository))
            .OnResultExecutionAsync(context, () => Task.FromResult(new ResultExecutedContext(
                context, context.Filters, context.Result, controller: null!)));

        return context;
    }

    private static async Task<ErrorResponse> Run(
        IActionResult result,
        string? acceptLanguage,
        Guid? userId = null,
        string? storedLanguage = null)
    {
        var context = await RunContext(result, acceptLanguage, userId, storedLanguage);

        var objectResult = context.Result.Should().BeOfType<ObjectResult>().Subject;
        return objectResult.Value.Should().BeOfType<ErrorResponse>().Subject;
    }

    [Fact]
    public async Task EnglishRequest_GetsTheEnglishCopy()
    {
        var body = await Run(Result.Failure(ErrorMessages.HabitNotFound).ToErrorResult(), "en-US,en;q=0.9");

        body.Error.Should().Be(ErrorCopy.All[ErrorCodes.HabitNotFound].En);
        body.ErrorCode.Should().Be(ErrorCodes.HabitNotFound);
    }

    [Fact]
    public async Task PortugueseRequest_GetsThePortugueseCopy()
    {
        var body = await Run(Result.Failure(ErrorMessages.HabitNotFound).ToErrorResult(), "pt-BR,pt;q=0.9,en;q=0.8");

        body.Error.Should().Be(ErrorCopy.All[ErrorCodes.HabitNotFound].PtBr);
        body.ErrorCode.Should().Be(ErrorCodes.HabitNotFound);
    }

    [Fact]
    public async Task MissingAcceptLanguage_FallsBackToEnglish()
    {
        var body = await Run(Result.Failure(ErrorMessages.HabitNotFound).ToErrorResult(), acceptLanguage: null);

        body.Error.Should().Be(ErrorCopy.All[ErrorCodes.HabitNotFound].En);
    }

    /// <summary>
    /// The Android app builds its headers from <c>buildClientTimeZoneHeaders</c> and
    /// <c>buildAppVersionHeaders</c> and sends no <c>Accept-Language</c>, and the web app reaches
    /// this API through Server Actions that send none either. A header-only rule answered every
    /// one of those readers in English.
    /// </summary>
    [Fact]
    public async Task APortugueseAccountSendingNoHeader_GetsThePortugueseCopy()
    {
        var body = await Run(
            Result.Failure(ErrorMessages.HabitNotFound).ToErrorResult(),
            acceptLanguage: null,
            userId: Guid.NewGuid(),
            storedLanguage: "pt-BR");

        body.Error.Should().Be(ErrorCopy.All[ErrorCodes.HabitNotFound].PtBr);
    }

    [Fact]
    public async Task TheStoredLanguageWinsOverTheHeader()
    {
        var body = await Run(
            Result.Failure(ErrorMessages.HabitNotFound).ToErrorResult(),
            acceptLanguage: "en-US,en;q=0.9",
            userId: Guid.NewGuid(),
            storedLanguage: "pt-BR");

        body.Error.Should().Be(ErrorCopy.All[ErrorCodes.HabitNotFound].PtBr);
    }

    [Fact]
    public async Task AnAccountWithNoStoredLanguage_FallsBackToTheHeader()
    {
        var body = await Run(
            Result.Failure(ErrorMessages.HabitNotFound).ToErrorResult(),
            acceptLanguage: "pt-BR",
            userId: Guid.NewGuid(),
            storedLanguage: null);

        body.Error.Should().Be(ErrorCopy.All[ErrorCodes.HabitNotFound].PtBr);
    }

    [Fact]
    public async Task ADomainGuardSentenceNeverReachesTheWire()
    {
        var guard = DomainErrors.DaysRequireQuantityOne;

        var english = await Run(Result.Failure(guard).ToErrorResult(), "en");
        var portuguese = await Run(Result.Failure(guard).ToErrorResult(), "pt-BR");

        english.Error.Should().NotBe(guard.Message);
        portuguese.Error.Should().NotBe(guard.Message);
        english.ErrorCode.Should().Be(guard.Code);
        portuguese.ErrorCode.Should().Be(guard.Code);
    }

    [Fact]
    public async Task APlaceholderIsFilledFromTheResultArguments()
    {
        var body = await Run(Result.Failure(ErrorMessages.MaxTagsPerHabit.Format(4)).ToErrorResult(), "pt-BR");

        body.Error.Should().Contain("4").And.NotContain("{0}");
    }

    /// <summary>
    /// Orbit 1.3.31 branches the step-up screen on <c>INVALID_VERIFICATION_CODE</c> and reads the
    /// count out of the message with <c>/remaining attempts:\s*(\d+)\s*$/i</c>, so both the code
    /// and the trailing token are a wire contract in either language.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task TheAttemptsCountStaysReadableByTheShippedClients(string acceptLanguage)
    {
        var body = await Run(
            Result.Failure(ErrorMessages.InvalidDeletionCode.Format(2)).ToErrorResult(), acceptLanguage);

        body.ErrorCode.Should().Be(ErrorCodes.InvalidVerificationCode);
        body.Error.Should().MatchRegex(@"[Rr]emaining attempts:\s*2\s*$");
    }

    [Fact]
    public async Task TheStatusCodeIsUntouched()
    {
        var context = await RunContext(
            Result.Failure(ErrorMessages.HabitNotFound).ToErrorResult(StatusCodes.Status404NotFound), "pt-BR");

        context.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task ANonErrorResultPassesThroughUntouched()
    {
        var original = new OkObjectResult(new { value = 1 });

        var context = await RunContext(original, acceptLanguage: null);

        context.Result.Should().BeSameAs(original);
    }
}
