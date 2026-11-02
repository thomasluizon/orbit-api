using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Sync.Commands;

namespace Orbit.Application.Sync.Validators;

public class ProcessSyncBatchCommandValidator : AbstractValidator<ProcessSyncBatchCommand>
{
    public ProcessSyncBatchCommandValidator()
    {
        RuleFor(command => command.UserId).NotEmpty();
        RuleFor(command => command.Mutations).NotEmpty()
            .Must(mutations => mutations.Count <= AppConstants.MaxBulkOperationSize);
    }
}
