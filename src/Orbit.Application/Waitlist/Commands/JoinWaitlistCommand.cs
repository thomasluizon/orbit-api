using MediatR;
using Hangfire;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Waitlist.Commands;

public record JoinWaitlistCommand(string Email, string Language = "en") : IRequest<Result>;

public class JoinWaitlistCommandHandler(
    IMemoryCache cache,
    IWaitlistConfirmationTokenService tokenService,
    IBackgroundJobClient jobs,
    IOptions<WaitlistSettings> waitlistOptions) : IRequestHandler<JoinWaitlistCommand, Result>
{
    private readonly WaitlistSettings _settings = waitlistOptions.Value;

    public Task<Result> Handle(JoinWaitlistCommand request, CancellationToken cancellationToken)
    {
        if (!WaitlistLanguage.TryCanonicalize(request.Language, out var language))
            return Task.FromResult(Result.Failure("Unsupported waitlist language."));

        var email = request.Email.Trim().ToLowerInvariant();
        var cacheKey = $"waitlist:{email}";

        if (cache.TryGetValue(cacheKey, out DateTime lastSentAt) &&
#pragma warning disable ORBIT0004 // The lastSentAt comparison enforces a 60-second waitlist email resend interval against a cached UTC instant. https://github.com/thomasluizon/orbit-tickets/issues/230
            (DateTime.UtcNow - lastSentAt).TotalSeconds < 60)
#pragma warning restore ORBIT0004
            return Task.FromResult(Result.Success());

        var token = tokenService.CreateToken(email, language);
        var confirmUrl = $"{_settings.ApiBaseUrl.TrimEnd('/')}/api/waitlist/confirm?token={Uri.EscapeDataString(token)}";

        jobs.Enqueue<SendWaitlistConfirmationJob>(job => job.ExecuteAsync(email, confirmUrl, language, CancellationToken.None));

        cache.Set(cacheKey, DateTime.UtcNow, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
        });

        return Task.FromResult(Result.Success());
    }
}
