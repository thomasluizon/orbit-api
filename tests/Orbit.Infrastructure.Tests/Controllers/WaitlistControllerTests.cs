using System.Text.Json;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Application.Waitlist.Commands;
using Orbit.Application.Waitlist.Validators;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Tests.Controllers;

public class WaitlistControllerTests
{
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IWaitlistConfirmationTokenService _tokenService = Substitute.For<IWaitlistConfirmationTokenService>();
    private readonly IOptions<WaitlistSettings> _settings = Options.Create(new WaitlistSettings
    {
        SigningKey = "test-signing-key",
        ApiBaseUrl = "https://api.useorbit.org",
        LandingBaseUrl = "https://useorbit.org"
    });

    public WaitlistControllerTests()
    {
        _tokenService.CreateToken(Arg.Any<string>(), Arg.Any<string>()).Returns("tok.sig");
    }

    [Fact]
    public async Task Join_ValidEmail_ReturnsOkAndQueuesConfirmationEmail()
    {
        var controller = BuildControllerWithRealPipeline();

        var result = await controller.Join(
            new WaitlistController.JoinWaitlistRequest("User@Test.com", "en"), CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        JsonSerializer.Serialize(ok.Value).Should().Contain("Check your inbox");
        await _emailService.Received(1).SendWaitlistConfirmationAsync(
            "user@test.com",
            "https://api.useorbit.org/api/waitlist/confirm?token=tok.sig",
            "en",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Join_DuplicateWithinCooldown_ReturnsOkButQueuesEmailOnce()
    {
        var controller = BuildControllerWithRealPipeline();
        var request = new WaitlistController.JoinWaitlistRequest("dupe@test.com", "en");

        var first = await controller.Join(request, CancellationToken.None);
        var second = await controller.Join(request, CancellationToken.None);

        first.Should().BeOfType<OkObjectResult>();
        second.Should().BeOfType<OkObjectResult>();
        await _emailService.Received(1).SendWaitlistConfirmationAsync(
            "dupe@test.com", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("")]
    public async Task Join_InvalidEmail_RejectedByValidationPipeline_DoesNotQueueEmail(string email)
    {
        var controller = BuildControllerWithRealPipeline();

        var act = () => controller.Join(
            new WaitlistController.JoinWaitlistRequest(email, "en"), CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
        await _emailService.DidNotReceive().SendWaitlistConfirmationAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Join_UnsupportedLanguage_RejectedByValidationPipeline_DoesNotQueueEmail()
    {
        var controller = BuildControllerWithRealPipeline();

        var act = () => controller.Join(
            new WaitlistController.JoinWaitlistRequest("user@test.com", "fr"), CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
        await _emailService.DidNotReceive().SendWaitlistConfirmationAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Confirm_ValidToken_RedirectsWithStatusOk()
    {
        var controller = BuildControllerWithMockedMediator(Result.Success());

        var result = await controller.Confirm("valid-token", CancellationToken.None);

        var redirect = result.Should().BeOfType<RedirectResult>().Subject;
        redirect.Url.Should().Be("https://useorbit.org/waitlist-confirmed?status=ok");
    }

    [Fact]
    public async Task Confirm_InvalidToken_RedirectsWithStatusInvalid()
    {
        var controller = BuildControllerWithMockedMediator(
            Result.Failure("Invalid or expired confirmation link."));

        var result = await controller.Confirm("bad-token", CancellationToken.None);

        var redirect = result.Should().BeOfType<RedirectResult>().Subject;
        redirect.Url.Should().Be("https://useorbit.org/waitlist-confirmed?status=invalid");
    }

    [Fact]
    public async Task Confirm_ForwardsTokenToCommand()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ConfirmWaitlistCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var controller = NewController(mediator, _settings);

        await controller.Confirm("the-token", CancellationToken.None);

        await mediator.Received(1).Send(
            Arg.Is<ConfirmWaitlistCommand>(c => c.Token == "the-token"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Confirm_TrimsTrailingSlashOnLandingBaseUrl()
    {
        var settings = Options.Create(new WaitlistSettings { LandingBaseUrl = "https://useorbit.org/" });
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ConfirmWaitlistCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var controller = NewController(mediator, settings);

        var result = await controller.Confirm("t", CancellationToken.None);

        var redirect = result.Should().BeOfType<RedirectResult>().Subject;
        redirect.Url.Should().Be("https://useorbit.org/waitlist-confirmed?status=ok");
    }

    private WaitlistController BuildControllerWithRealPipeline()
    {
        var provider = new ServiceCollection()
            .AddLogging()
            .AddMediatR(cfg =>
            {
                cfg.RegisterServicesFromAssemblyContaining<JoinWaitlistCommand>();
                cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
            })
            .AddScoped<IValidator<JoinWaitlistCommand>, JoinWaitlistCommandValidator>()
            .AddMemoryCache()
            .AddSingleton(_emailService)
            .AddSingleton(_tokenService)
            .AddSingleton(_settings)
            .BuildServiceProvider();

        return NewController(provider.GetRequiredService<IMediator>(), _settings);
    }

    private WaitlistController BuildControllerWithMockedMediator(Result confirmResult)
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ConfirmWaitlistCommand>(), Arg.Any<CancellationToken>())
            .Returns(confirmResult);
        return NewController(mediator, _settings);
    }

    private static WaitlistController NewController(IMediator mediator, IOptions<WaitlistSettings> settings)
    {
        return new WaitlistController(mediator, settings, NullLogger<WaitlistController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }
}
