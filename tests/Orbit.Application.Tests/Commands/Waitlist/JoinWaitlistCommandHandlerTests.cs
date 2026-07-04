using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Waitlist.Commands;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Waitlist;

public class JoinWaitlistCommandHandlerTests
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly IWaitlistConfirmationTokenService _tokenService = Substitute.For<IWaitlistConfirmationTokenService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly JoinWaitlistCommandHandler _handler;

    public JoinWaitlistCommandHandlerTests()
    {
        _tokenService.CreateToken(Arg.Any<string>(), Arg.Any<string>()).Returns("tok.sig");
        var settings = Options.Create(new WaitlistSettings
        {
            SigningKey = "k",
            ApiBaseUrl = "https://api.useorbit.org"
        });
        _handler = new JoinWaitlistCommandHandler(_cache, _tokenService, _emailService, settings);
    }

    [Fact]
    public async Task Handle_NewEmail_MintsTokenAndSendsConfirmationEmail()
    {
        var result = await _handler.Handle(new JoinWaitlistCommand("User@Test.com", "en"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _tokenService.Received(1).CreateToken("user@test.com", "en");
        await _emailService.Received(1).SendWaitlistConfirmationAsync(
            "user@test.com",
            "https://api.useorbit.org/api/waitlist/confirm?token=tok.sig",
            "en",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithinCooldown_DoesNotSendSecondEmail()
    {
        await _handler.Handle(new JoinWaitlistCommand("user@test.com"), CancellationToken.None);
        var second = await _handler.Handle(new JoinWaitlistCommand("user@test.com"), CancellationToken.None);

        second.IsSuccess.Should().BeTrue();
        await _emailService.Received(1).SendWaitlistConfirmationAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_DifferentEmails_EachReceivesEmail()
    {
        await _handler.Handle(new JoinWaitlistCommand("a@test.com"), CancellationToken.None);
        await _handler.Handle(new JoinWaitlistCommand("b@test.com"), CancellationToken.None);

        await _emailService.Received(1).SendWaitlistConfirmationAsync(
            "a@test.com", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _emailService.Received(1).SendWaitlistConfirmationAsync(
            "b@test.com", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
