using System.Text.Json;
using FluentAssertions;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Validators;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public sealed class ChatTurnIdentityTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("turn-key", true)]
    public void KeyValidator_RequiresNonBlankBoundedKey(string key, bool expected) =>
        new ChatTurnKeyValidator().Validate(key).IsValid.Should().Be(expected);

    [Fact]
    public void KeyValidator_EnforcesMaximumLength()
    {
        var validator = new ChatTurnKeyValidator();
        validator.Validate(new string('a', 200)).IsValid.Should().BeTrue();
        validator.Validate(new string('a', 201)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Fingerprint_ExcludesTransportIdentityAndIncludesTurnInputs()
    {
        var command = new ProcessUserChatCommand(Guid.NewGuid(), "Hello", [1, 2], "image/png");
        var original = ChatTurnIdentity.Fingerprint(command);
        ChatTurnIdentity.Fingerprint(command with { CorrelationId = "retry", TurnId = Guid.NewGuid(), StreamSink = _ => Task.CompletedTask })
            .Should().Be(original);
        ChatTurnIdentity.Fingerprint(command with { Message = "Changed" }).Should().NotBe(original);
        ChatTurnIdentity.Fingerprint(command with { ImageData = [2, 1] }).Should().NotBe(original);
        ChatTurnIdentity.Fingerprint(command with { ImageMimeType = "image/jpeg" }).Should().NotBe(original);
        ChatTurnIdentity.Fingerprint(command with { History = [new("user", "History")] }).Should().NotBe(original);
        ChatTurnIdentity.Fingerprint(command with { ConfirmationToken = "confirmation" }).Should().NotBe(original);
        ChatTurnIdentity.Fingerprint(command with { IsReadOnlyCredential = true }).Should().NotBe(original);
        ChatTurnIdentity.Fingerprint(command with { EvalTraceRequested = true }).Should().NotBe(original);
        ChatTurnIdentity.Fingerprint(command with { BlockAnswer = new(Guid.NewGuid(), ["choice"]) }).Should().NotBe(original);
    }
}
