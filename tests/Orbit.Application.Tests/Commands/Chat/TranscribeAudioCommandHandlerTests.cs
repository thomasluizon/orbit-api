using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Chat;

public class TranscribeAudioCommandHandlerTests
{
    private readonly IAudioTranscriptionService _transcription = Substitute.For<IAudioTranscriptionService>();
    private readonly IGenericRepository<User> _users = Substitute.For<IGenericRepository<User>>();
    private readonly TranscribeAudioCommandHandler _handler;
    private readonly Guid _userId = Guid.NewGuid();

    public TranscribeAudioCommandHandlerTests()
    {
        _handler = new TranscribeAudioCommandHandler(_transcription, _users);
    }

    [Fact]
    public async Task Handle_TranscriptionSucceeds_ReturnsText()
    {
        _transcription.TranscribeAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("Walk the dog every morning."));

        var command = new TranscribeAudioCommand(_userId, [1, 2, 3], "clip.webm");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Text.Should().Be("Walk the dog every morning.");
    }

    [Fact]
    public async Task Handle_TranscriptionFails_PropagatesError()
    {
        _transcription.TranscribeAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<string>(ErrorMessages.AudioTranscriptionEmpty));

        var command = new TranscribeAudioCommand(_userId, [1, 2, 3], "clip.webm");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.AudioTranscriptionEmpty);
    }

    [Theory]
    [InlineData("pt-BR", "pt")]
    [InlineData("en", "en")]
    public async Task Handle_UsesAccountLanguageForTranscription(string accountLanguage, string expectedHint)
    {
        var userId = Guid.NewGuid();
        var user = User.Create("Test User", "test@example.com").Value;
        user.SetLanguage(accountLanguage);
        var users = Substitute.For<IGenericRepository<User>>();
        users.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);
        var handler = new TranscribeAudioCommandHandler(_transcription, users);
        _transcription.TranscribeAsync(Arg.Any<Stream>(), Arg.Any<string>(), expectedHint, Arg.Any<CancellationToken>())
            .Returns(Result.Success("Transcribed text"));

        var result = await handler.Handle(new TranscribeAudioCommand(userId, [1, 2, 3], "clip.webm"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _transcription.Received(1).TranscribeAsync(
            Arg.Any<Stream>(), "clip.webm", expectedHint, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("fr")]
    public async Task Handle_UnsupportedAccountLanguage_OmitsHint(string? accountLanguage)
    {
        var user = User.Create("Test User", "test@example.com").Value;
        user.SetLanguage(accountLanguage);
        _users.GetByIdAsync(_userId, Arg.Any<CancellationToken>()).Returns(user);
        _transcription.TranscribeAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Is<string?>(language => language == null), Arg.Any<CancellationToken>())
            .Returns(Result.Success("Transcribed text"));

        var result = await _handler.Handle(new TranscribeAudioCommand(_userId, [1, 2, 3], "clip.webm"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _transcription.Received(1).TranscribeAsync(
            Arg.Any<Stream>(), Arg.Is("clip.webm"), Arg.Is<string?>(language => language == null), Arg.Any<CancellationToken>());
    }
}
