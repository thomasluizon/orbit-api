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
            .WithMessage("FactIds list must not be empty")
            .Must(ids => ids.Count <= AppConstants.MaxBulkOperationSize)
            .WithMessage($"Cannot delete more than {AppConstants.MaxBulkOperationSize} facts at once");

        RuleForEach(x => x.FactIds)
            .NotEmpty()
            .WithMessage("Fact ID must not be empty");
    }
}
