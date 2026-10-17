using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.UserFacts.Commands;

namespace Orbit.Application.UserFacts.Validators;

public class BulkDeleteUserFactsCommandValidator : AbstractValidator<BulkDeleteUserFactsCommand>
{
    public BulkDeleteUserFactsCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.FactIds)
            .NotEmpty()
            .WithCopy(ValidationErrorCodes.FactIdsRequired)
            .Must(ids => ids.Count <= AppConstants.MaxBulkOperationSize)
            .WithCopy(ValidationErrorCodes.BulkDeleteFactLimit);

        RuleForEach(x => x.FactIds)
            .NotEmpty()
            .WithCopy(ValidationErrorCodes.FactIdRequired);
    }
}
