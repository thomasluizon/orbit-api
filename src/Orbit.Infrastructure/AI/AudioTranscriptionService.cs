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
using System.Security.Claims;

namespace Orbit.Infrastructure.AI;

public sealed partial class AudioTranscriptionService : IAudioTranscriptionService
{
    private readonly AudioClient _audioClient;
    private readonly ILogger<AudioTranscriptionService> _logger;
    private readonly IAiDailyBudget? _budget;
    private readonly Guid? _evalUserId;
    private readonly string _model;

    public AudioTranscriptionService(IOptions<AiSettings> options, ILogger<AudioTranscriptionService> logger,
        IConfiguration? configuration = null, IHttpContextAccessor? http = null, IAiDailyBudget? budget = null)
        : this(options, logger, null, configuration, http, budget)
    {
    }

    internal AudioTranscriptionService(IOptions<AiSettings> options, ILogger<AudioTranscriptionService> logger,
        PipelineTransport? transport, IConfiguration? configuration = null, IHttpContextAccessor? http = null,
        IAiDailyBudget? budget = null)
    {
        _logger = logger;
        _budget = budget;
        var settings = options.Value;
        _model = settings.TranscriptionModel;
        var userId = AiCallContext.Current?.UserId;
        if (userId is null && Guid.TryParse(http?.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var authenticatedUser))
            userId = authenticatedUser;
        if (userId is { } id && EvalAccountAccess.Contains(configuration?[EvalAccountAccess.AccountIdsKey], id))
            _evalUserId = id;

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(settings.BaseUrl),
            Transport = transport,
            NetworkTimeout = TimeSpan.FromSeconds(settings.NetworkTimeoutSeconds),
            RetryPolicy = new AiRetryLoggingPolicy(_evalUserId is null ? settings.MaxRetries : 0, logger)
        };
        clientOptions.AddPolicy(new AiBudgetDispatchPolicy(), PipelinePosition.BeforeTransport);

        _audioClient = new AudioClient(
            model: settings.TranscriptionModel,
            credential: new ApiKeyCredential(AiEvalCredentials.Resolve(settings, configuration, http)),
            options: clientOptions);
    }

    public async Task<Result<string>> TranscribeAsync(Stream audio, string fileName, string? language, CancellationToken cancellationToken = default)
    {
        Guid? reservation = null;
        long reservedMicroDollars = 0;
        using var context = _evalUserId is { } userId ? AiCallContext.Begin(userId) : null;
        try
        {
            if (_evalUserId is { } evalUser)
            {
                if (_budget is null)
                    throw new InvalidOperationException("Evaluation audio requires spend reservation.");
                reservedMicroDollars = EvalAudioReservation.EstimateMicroDollars(audio, fileName, _model);
                reservation = await _budget.ReserveAsync(evalUser, reservedMicroDollars, cancellationToken);
            }
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
        catch (AiBudgetExceededException)
        {
            return Result.Failure<string>(AiBudgetExceededException.Error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogTranscriptionFailed(_logger, ex);
            return Result.Failure<string>(ErrorMessages.AudioTranscriptionFailed);
        }
        finally
        {
            if (reservation is { } callId && _budget is { } dailyBudget && context is { } evalContext)
            {
                using var settlement = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    await dailyBudget.SettleAsync(callId, evalContext.HasUnreportedDispatch ? reservedMicroDollars : 0, settlement.Token);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "AI audio budget settlement failed for {CallId}; spend reservation retained", callId);
                }
            }
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Audio transcription failed")]
    private static partial void LogTranscriptionFailed(ILogger logger, Exception ex);
}
