using System.Security.Cryptography;
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
        // Format: TEST_ACCOUNTS=email1:code1,email2:code2,...
        var testAccountsEnv = Environment.GetEnvironmentVariable("TEST_ACCOUNTS");
        if (!string.IsNullOrEmpty(testAccountsEnv))
        {
            foreach (var pair in testAccountsEnv.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split(':', 2);
                if (parts.Length == 2 && string.Equals(parts[0].Trim(), email, StringComparison.OrdinalIgnoreCase))
                {
                    var testEntry = new VerificationEntry(parts[1].Trim(), 0, DateTime.UtcNow);
                    cache.Set(cacheKey, testEntry, new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30)
                    });
                    return Result.Success();
                }
            }
        }

        // Rate limit: no new code within 60 seconds
        if (cache.TryGetValue(cacheKey, out VerificationEntry? existing) && existing is not null)
        {
            var elapsed = DateTime.UtcNow - existing.CreatedAt;
            if (elapsed.TotalSeconds < 60)
                return Result.Failure("Please wait before requesting a new code");
        }

        // Generate 6-digit code using cryptographic PRNG
        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();

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
