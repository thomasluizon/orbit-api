using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Chat;

public class TranscribeAudioCommandHandlerTests
{
    private readonly IAudioTranscriptionService _transcription = Substitute.For<IAudioTranscriptionService>();
    private readonly TranscribeAudioCommandHandler _handler;

    public TranscribeAudioCommandHandlerTests()
    {
        _handler = new TranscribeAudioCommandHandler(_transcription);
    }

    [Fact]
    public async Task Handle_TranscriptionSucceeds_ReturnsText()
    {
        _transcription.TranscribeAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("Walk the dog every morning."));

        var command = new TranscribeAudioCommand([1, 2, 3], "clip.webm");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Text.Should().Be("Walk the dog every morning.");
    }

    [Fact]
    public async Task Handle_TranscriptionFails_PropagatesError()
    {
        _transcription.TranscribeAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<string>(ErrorMessages.AudioTranscriptionEmpty));

        var command = new TranscribeAudioCommand([1, 2, 3], "clip.webm");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.AudioTranscriptionEmpty);
    }
}
