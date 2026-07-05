using FluentValidation;
using Orbit.Application.Marketing.Commands;

namespace Orbit.Application.Marketing.Validators;

public class SendMarketingBroadcastCommandValidator : AbstractValidator<SendMarketingBroadcastCommand>
{
    private const int MaxSubjectLength = 200;
    private const int MaxBodyLength = 100_000;

    public SendMarketingBroadcastCommandValidator()
    {
        RuleFor(command => command.SubjectEn).NotEmpty().MaximumLength(MaxSubjectLength);
        RuleFor(command => command.SubjectPt).NotEmpty().MaximumLength(MaxSubjectLength);
        RuleFor(command => command.BodyHtmlEn).NotEmpty().MaximumLength(MaxBodyLength);
        RuleFor(command => command.BodyHtmlPt).NotEmpty().MaximumLength(MaxBodyLength);

        When(command => !string.IsNullOrWhiteSpace(command.TestEmail), () =>
            RuleFor(command => command.TestEmail).EmailAddress());
    }
}
