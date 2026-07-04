using FluentAssertions;
using NSubstitute;
using Orbit.Application.Waitlist.Commands;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Waitlist;

public class ConfirmWaitlistCommandHandlerTests
{
    private readonly IWaitlistConfirmationTokenService _tokenService = Substitute.For<IWaitlistConfirmationTokenService>();
    private readonly IMarketingContactsService _contactsService = Substitute.For<IMarketingContactsService>();
    private readonly ConfirmWaitlistCommandHandler _handler;

    public ConfirmWaitlistCommandHandlerTests()
    {
        _handler = new ConfirmWaitlistCommandHandler(_tokenService, _contactsService);
    }

    [Fact]
    public async Task Handle_ValidToken_AddsContactAndSucceeds()
    {
        _tokenService
            .TryValidateToken("good-token", out Arg.Any<string>(), out Arg.Any<string>())
            .Returns(call =>
            {
                call[1] = "user@test.com";
                call[2] = "en";
                return true;
            });

        var result = await _handler.Handle(new ConfirmWaitlistCommand("good-token"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _contactsService.Received(1).AddContactAsync("user@test.com", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_InvalidToken_FailsWithoutAddingContact()
    {
        _tokenService
            .TryValidateToken("bad-token", out Arg.Any<string>(), out Arg.Any<string>())
            .Returns(false);

        var result = await _handler.Handle(new ConfirmWaitlistCommand("bad-token"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        await _contactsService.DidNotReceive().AddContactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
