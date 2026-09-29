using Orbit.Domain.Common;

namespace Orbit.Domain.Interfaces;

public interface IAudioTranscriptionService
{
    Task<Result<string>> TranscribeAsync(Stream audio, string fileName, string? language, CancellationToken cancellationToken = default);
}
