using FluentValidation;
using Orbit.Application.Uploads.Commands;
using Orbit.Application.Uploads.Common;

namespace Orbit.Application.Uploads.Validators;

public class SignUploadValidator : AbstractValidator<SignUploadCommand>
{
    public SignUploadValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.ContentType)
            .NotEmpty()
            .WithMessage("Content type is required.")
            .Must(UploadContentTypes.IsAllowed)
            .WithMessage($"Content type must be one of: {string.Join(", ", UploadContentTypes.Allowed)}.");

        RuleFor(x => x.SizeBytes)
            .GreaterThan(0)
            .WithMessage("File size must be greater than zero.")
            .LessThanOrEqualTo(UploadContentTypes.MaxSizeBytes)
            .WithMessage($"File size must be {UploadContentTypes.MaxSizeBytes} bytes or less.");
    }
}
