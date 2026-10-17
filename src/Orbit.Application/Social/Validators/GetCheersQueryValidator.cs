using Orbit.Application.Common;
using FluentValidation;
using Orbit.Application.Social.Queries;

namespace Orbit.Application.Social.Validators;

public class GetCheersQueryValidator : AbstractValidator<GetCheersQuery>
{
    private static readonly string[] AllowedDirections = ["received", "sent"];

    public GetCheersQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Direction)
            .Must(direction => AllowedDirections.Contains(direction))
            .WithCopy(ValidationErrorCodes.CheersDirection);
    }
}
