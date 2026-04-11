using FluentAssertions;
using NSubstitute;
using Orbit.Application.Support.Commands;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Support;

public class SendSupportCommandHandlerTests
{
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly SendSupportCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public SendSupportCommandHandlerTests()
    {
        _handler = new SendSupportCommandHandler(_emailService);
    }

    [Fact]
    public async Task Handle_ValidCommand_SendsEmail()
    {
        var command = new SendSupportCommand(UserId, "John", "john@test.com", "Bug Report", "Something broke");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _emailService.Received(1).SendSupportEmailAsync(
            "John", "john@test.com", "Bug Report", "Something broke", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_EmptySubject_ReturnsFailure()
    {
        var command = new SendSupportCommand(UserId, "John", "john@test.com", "", "Message body");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Subject");
        await _emailService.DidNotReceive().SendSupportEmailAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhitespaceSubject_ReturnsFailure()
    {
        var command = new SendSupportCommand(UserId, "John", "john@test.com", "   ", "Message body");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_EmptyMessage_ReturnsFailure()
    {
        var command = new SendSupportCommand(UserId, "John", "john@test.com", "Subject", "");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Message");
    }

    [Fact]
    public async Task Handle_WhitespaceMessage_ReturnsFailure()
    {
        var command = new SendSupportCommand(UserId, "John", "john@test.com", "Subject", "   ");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }
}
