using Hangfire;
using MediatR;
using Microsoft.Extensions.Hosting;
using Orbit.Application.Auth.Services;
using Orbit.Application.Auth.Jobs;
using Orbit.Application.Common;
using Orbit.Domain.Common;

namespace Orbit.Application.Auth.Commands;

public record SendCodeCommand(string Email, string Language = "en")
    : IRequest<Result>;

public class SendCodeCommandHandler(
    IEmailChallengeStore challenges,
    IHostEnvironment environment,
    IBackgroundJobClient backgroundJobClient) : IRequestHandler<SendCodeCommand, Result>
{
    public async Task<Result> Handle(SendCodeCommand request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var fixedCode = (environment.IsProduction() ? ProductionSignInCode.For(email) : null)
            ?? TrySeedTestAccountCode(email);
        var issued = await challenges.IssueAsync(EmailChallengeOperation.SignIn,
            email, fixedCode, cancellationToken: cancellationToken);
        if (issued.IsFailure)
            return issued.PropagateError();
        if (fixedCode is null)
            backgroundJobClient.Enqueue<SendVerificationCodeEmailJob>(
                job => job.ExecuteAsync(email, issued.Value, request.Language));
        return Result.Success();
    }

    private string? TrySeedTestAccountCode(string email)
    {
        if (!environment.IsDevelopment()
            && !environment.IsStaging())
            return null;
        foreach (var pair in (Environment.GetEnvironmentVariable("TEST_ACCOUNTS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split(':', 2);
            if (parts.Length == 2 && string.Equals(parts[0].Trim(), email, StringComparison.OrdinalIgnoreCase))
            {
                var code = parts[1].Trim();
                if (code.Length == 6 && code.All(char.IsAsciiDigit))
                    return code;
            }
        }
        return null;
    }
}
