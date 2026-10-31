using System.ClientModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Audio;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using System.ClientModel.Primitives;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Orbit.Infrastructure.AI;

public sealed partial class AudioTranscriptionService : IAudioTranscriptionService
{
    private readonly AudioClient _audioClient;
    private readonly ILogger<AudioTranscriptionService> _logger;

    public AudioTranscriptionService(IOptions<AiSettings> options, ILogger<AudioTranscriptionService> logger,
        IConfiguration? configuration = null, IHttpContextAccessor? http = null)
        : this(options, logger, null, configuration, http)
    {
    }

    internal AudioTranscriptionService(IOptions<AiSettings> options, ILogger<AudioTranscriptionService> logger,
        PipelineTransport? transport, IConfiguration? configuration = null, IHttpContextAccessor? http = null)
    {
        _logger = logger;
        var settings = options.Value;

        _audioClient = new AudioClient(
            model: settings.TranscriptionModel,
            credential: new ApiKeyCredential(AiEvalCredentials.Resolve(settings, configuration, http)),
            options: new OpenAIClientOptions
            {
                Endpoint = new Uri(settings.BaseUrl),
                Transport = transport,
                NetworkTimeout = TimeSpan.FromSeconds(settings.NetworkTimeoutSeconds),
                RetryPolicy = new AiRetryLoggingPolicy(settings.MaxRetries, logger)
            });
    }

    public async Task<Result<string>> TranscribeAsync(Stream audio, string fileName, string? language, CancellationToken cancellationToken = default)
    {
        try
        {
            var transcription = await _audioClient.TranscribeAudioAsync(
                audio,
                fileName,
                new AudioTranscriptionOptions { Language = language },
                cancellationToken);

            var text = transcription.Value.Text?.Trim();

            if (string.IsNullOrWhiteSpace(text))
                return Result.Failure<string>(ErrorMessages.AudioTranscriptionEmpty);

            return Result.Success(text);
        }
        catch (Exception ex)
        {
            LogTranscriptionFailed(_logger, ex);
            return Result.Failure<string>(ErrorMessages.AudioTranscriptionFailed);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Audio transcription failed")]
    private static partial void LogTranscriptionFailed(ILogger logger, Exception ex);
}
