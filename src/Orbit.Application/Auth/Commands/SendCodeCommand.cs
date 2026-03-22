using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Commands;

public record SendCodeCommand(string Email, string Language = "en")
    : IRequest<Result>;

public record VerificationEntry(string Code, int Attempts, DateTime CreatedAt);

public class SendCodeCommandHandler(
    IMemoryCache cache,
    IEmailService emailService) : IRequestHandler<SendCodeCommand, Result>
{
    public async Task<Result> Handle(SendCodeCommand request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var cacheKey = $"verify:{email}";

        // Test accounts: skip email, store fixed code
        var testAccounts = new[]
        {
            (Environment.GetEnvironmentVariable("REVIEWER_TEST_EMAIL")?.ToLowerInvariant(),
             Environment.GetEnvironmentVariable("REVIEWER_TEST_CODE")),
            (Environment.GetEnvironmentVariable("QA_TEST_EMAIL")?.ToLowerInvariant(),
             Environment.GetEnvironmentVariable("QA_TEST_CODE")),
        };

        var matchedAccount = Array.Find(testAccounts, a => a.Item1 is not null && a.Item2 is not null && email == a.Item1);
        if (matchedAccount != default)
        {
            var testEntry = new VerificationEntry(matchedAccount.Item2!, 0, DateTime.UtcNow);
            cache.Set(cacheKey, testEntry, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30)
            });
            return Result.Success();
        }

        // Rate limit: no new code within 60 seconds
        if (cache.TryGetValue(cacheKey, out VerificationEntry? existing) && existing is not null)
        {
            var elapsed = DateTime.UtcNow - existing.CreatedAt;
            if (elapsed.TotalSeconds < 60)
                return Result.Failure("Please wait before requesting a new code");
        }

        // Generate 6-digit code
        var code = Random.Shared.Next(100000, 999999).ToString();

        // Store in cache with 5-minute expiration
        var entry = new VerificationEntry(code, 0, DateTime.UtcNow);
        cache.Set(cacheKey, entry, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
        });

        // Send code via email
        await emailService.SendVerificationCodeAsync(email, code, request.Language, cancellationToken);

        return Result.Success();
    }
}
