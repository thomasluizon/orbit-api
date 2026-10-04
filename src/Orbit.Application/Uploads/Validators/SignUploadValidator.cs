using Orbit.Application.Common;
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
            .WithCopy(ValidationErrorCodes.UploadContentTypeRequired)
            .Must(UploadContentTypes.IsAllowed)
            .WithCopy(ValidationErrorCodes.UploadContentTypeSupported);

        RuleFor(x => x.SizeBytes)
            .GreaterThan(0)
            .WithCopy(ValidationErrorCodes.UploadSizePositive)
            .LessThanOrEqualTo(UploadContentTypes.MaxSizeBytes)
            .WithCopy(ValidationErrorCodes.UploadSizeLimit);
    }
}
