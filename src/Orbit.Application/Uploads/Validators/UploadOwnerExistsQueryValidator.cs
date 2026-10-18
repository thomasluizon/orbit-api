using FluentValidation;
using Orbit.Application.Uploads.Queries;

namespace Orbit.Application.Uploads.Validators;

public sealed class UploadOwnerExistsQueryValidator : AbstractValidator<UploadOwnerExistsQuery>
{
    public UploadOwnerExistsQueryValidator()
    {
        RuleFor(query => query.UserId).NotEmpty();
    }
}
