using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Commands;

public record TranscribeAudioResponse(string Text);

public record TranscribeAudioCommand(Guid UserId, byte[] Audio, string FileName) : IRequest<Result<TranscribeAudioResponse>>;

public class TranscribeAudioCommandHandler(
    IAudioTranscriptionService transcription,
    IGenericRepository<User> users)
    : IRequestHandler<TranscribeAudioCommand, Result<TranscribeAudioResponse>>
{
    public async Task<Result<TranscribeAudioResponse>> Handle(TranscribeAudioCommand request, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(request.Audio);
        var user = await users.GetByIdAsync(request.UserId, cancellationToken);
        var language = user?.Language switch
        {
            "pt-BR" => "pt",
            "en" => "en",
            _ => null
        };
        var result = await transcription.TranscribeAsync(stream, request.FileName, language, cancellationToken);
        return result.IsSuccess
            ? Result.Success(new TranscribeAudioResponse(result.Value))
            : result.PropagateError<TranscribeAudioResponse>();
    }
}
