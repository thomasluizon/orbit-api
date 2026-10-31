using System.ClientModel;
using System.ClientModel.Primitives;
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
    private readonly AiSettings _settings;
    private readonly PipelineTransport? _transport;
    private readonly IFeatureFlagService _flags;
    private readonly IConfiguration? _configuration;
    private readonly IHttpContextAccessor? _http;
    private readonly ILogger<AudioTranscriptionService> _logger;
    private readonly IAiDailyBudget? _budget;

    public AudioTranscriptionService(IOptions<AiSettings> options, ILogger<AudioTranscriptionService> logger,
        IFeatureFlagService flags, IConfiguration? configuration = null, IHttpContextAccessor? http = null, IAiDailyBudget? budget = null)
        : this(options, logger, null, flags, configuration, http, budget)
    {
    }

    internal AudioTranscriptionService(IOptions<AiSettings> options, ILogger<AudioTranscriptionService> logger,
        PipelineTransport? transport, IFeatureFlagService flags, IConfiguration? configuration = null, IHttpContextAccessor? http = null,
        IAiDailyBudget? budget = null)
    {
        _logger = logger;
        _budget = budget;
        _settings = options.Value;
        _transport = transport;
        _flags = flags;
        _configuration = configuration;
        _http = http;
    }

    private AudioClient CreateClient(AiEvalAccount account)
    {
        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(_settings.BaseUrl),
            Transport = _transport,
            NetworkTimeout = TimeSpan.FromSeconds(_settings.NetworkTimeoutSeconds),
            RetryPolicy = new AiRetryLoggingPolicy(account.IsEvaluation ? 0 : _settings.MaxRetries, _logger)
        };
        clientOptions.AddPolicy(new AiBudgetDispatchPolicy(), PipelinePosition.BeforeTransport);

        return new AudioClient(
            model: _settings.TranscriptionModel,
            credential: new ApiKeyCredential(AiEvalCredentials.Resolve(_settings, account)),
            options: clientOptions);
    }

    public async Task<Result<string>> TranscribeAsync(Stream audio, string fileName, string? language, CancellationToken cancellationToken = default)
    {
        Guid? reservation = null;
        long reservedMicroDollars = 0;
        AiCallContext? context = null;
        try
        {
            var account = await AiEvalAccount.ResolveAsync(AiEvalAccount.CurrentUserId(_http), _flags,
                _configuration, cancellationToken);
            if (account.IsEvaluation && account.UserId is { } evalUser)
            {
                context = AiCallContext.Begin(evalUser);
                if (_budget is null)
                    throw new InvalidOperationException("Evaluation audio requires spend reservation.");
                reservedMicroDollars = EvalAudioReservation.EstimateMicroDollars(audio, fileName, _settings.TranscriptionModel);
                reservation = await _budget.ReserveAsync(evalUser, reservedMicroDollars, cancellationToken);
            }
            var transcription = await CreateClient(account).TranscribeAudioAsync(
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
        catch (Exception exception) when (AiProviderErrors.IsQuotaExhausted(exception))
        {
            return Result.Failure<string>(ErrorMessages.AiProviderQuotaExhausted);
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
            context?.Dispose();
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Audio transcription failed")]
    private static partial void LogTranscriptionFailed(ILogger logger, Exception ex);
}
