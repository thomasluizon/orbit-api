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

        var aspNetEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        if (!string.Equals(aspNetEnv, "Production", StringComparison.OrdinalIgnoreCase))
        {
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
        }

        if (cache.TryGetValue(cacheKey, out VerificationEntry? existing) && existing is not null)
        {
            var elapsed = DateTime.UtcNow - existing.CreatedAt;
            if (elapsed.TotalSeconds < 60)
                return Result.Failure(ErrorMessages.CodeRequestCooldown);
        }

        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();

        var entry = new VerificationEntry(code, 0, DateTime.UtcNow);
        cache.Set(cacheKey, entry, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
        });

        await emailService.SendVerificationCodeAsync(email, code, request.Language, cancellationToken);

        return Result.Success();
    }
}
