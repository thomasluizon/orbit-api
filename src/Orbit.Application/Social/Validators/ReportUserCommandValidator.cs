using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Social.Commands;

namespace Orbit.Application.Social.Validators;

public class ReportUserCommandValidator : AbstractValidator<ReportUserCommand>
{
    public ReportUserCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.ReportedUserId).NotEmpty().NotEqual(x => x.UserId);
        RuleFor(x => x.Reason).IsInEnum();
        RuleFor(x => x.Details).MaximumLength(AppConstants.MaxReportDetailsLength);
    }
}
