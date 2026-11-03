using FluentValidation;
using Orbit.Application.Auth.Services;

namespace Orbit.Application.Auth.Validators;

public sealed record EmailChallengeIssue(EmailChallengeOperation Operation, string Email, string? FixedCode);

public sealed class EmailChallengeIssueValidator : AbstractValidator<EmailChallengeIssue>
{
    public EmailChallengeIssueValidator()
    {
        RuleFor(request => request.Operation).IsInEnum();
        RuleFor(request => request.Email).NotEmpty().MaximumLength(320).EmailAddress();
        RuleFor(request => request.FixedCode).Matches("^[0-9]{6}$").When(request => request.FixedCode is not null);
    }
}
