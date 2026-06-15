using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Commands;

public record TranscribeAudioResponse(string Text);

public record TranscribeAudioCommand(byte[] Audio, string FileName) : IRequest<Result<TranscribeAudioResponse>>;

public class TranscribeAudioCommandHandler(IAudioTranscriptionService transcription)
    : IRequestHandler<TranscribeAudioCommand, Result<TranscribeAudioResponse>>
{
    public async Task<Result<TranscribeAudioResponse>> Handle(TranscribeAudioCommand request, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(request.Audio);
        var result = await transcription.TranscribeAsync(stream, request.FileName, cancellationToken);
        return result.IsSuccess
            ? Result.Success(new TranscribeAudioResponse(result.Value))
            : result.PropagateError<TranscribeAudioResponse>();
    }
}
