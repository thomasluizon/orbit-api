using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
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
    public async Task Handle_ValidRequest_SendsEmailAndReturnsSuccess()
    {
        var command = new SendSupportCommand(UserId, "Thomas", "test@example.com", "Bug Report", "Something is broken");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _emailService.Received(1).SendSupportEmailAsync(
            "Thomas", "test@example.com", "Bug Report", "Something is broken",
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Handle_EmptySubject_ReturnsFailure(string? subject)
    {
        var command = new SendSupportCommand(UserId, "Thomas", "test@example.com", subject!, "Message body");
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.SubjectRequired);
        await _emailService.DidNotReceive().SendSupportEmailAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Handle_EmptyMessage_ReturnsFailure(string? message)
    {
        var command = new SendSupportCommand(UserId, "Thomas", "test@example.com", "Subject", message!);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.MessageRequired);
    }

    [Fact]
    public async Task Handle_EmptySubjectAndMessage_ReturnsSubjectFailureFirst()
    {
        var command = new SendSupportCommand(UserId, "Thomas", "test@example.com", "", "");
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.SubjectRequired);
    }
}
