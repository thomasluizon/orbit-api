using Hangfire;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Orbit.Application.Auth.Jobs;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Services;

public sealed partial class SignupJobDispatcher(
    IBackgroundJobClient backgroundJobClient,
    IHttpContextAccessor httpContextAccessor,
    ILogger<SignupJobDispatcher> logger) : ISignupJobDispatcher
{
    public void Dispatch(Guid userId, bool signupPending, string language, string? referralCode)
    {
        try
        {
            backgroundJobClient.Enqueue<CompleteSignupJob>(job => job.ExecuteAsync(
                userId, signupPending, language, referralCode));
        }
        catch (BackgroundJobClientException exception)
        {
            LogDispatchFailed(logger, exception, userId, httpContextAccessor.HttpContext?.TraceIdentifier);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "Signup dispatch failed for user {UserId}. RequestId={RequestId}")]
    private static partial void LogDispatchFailed(ILogger logger, Exception exception, Guid userId, string? requestId);
}
