using FluentValidation;
using Orbit.Application.FeatureFlags.Commands;
using Orbit.Application.FeatureFlags.Queries;

namespace Orbit.Application.FeatureFlags.Validators;

public class ManageFeatureFlagsCommandValidator : AbstractValidator<ManageFeatureFlagsCommand>
{
    public ManageFeatureFlagsCommandValidator()
    {
        RuleFor(request => request.AdminId).NotEmpty();
        RuleFor(request => request.Action).Must(action => FeatureFlagActions.Writes.Contains(action));
        RuleFor(request => request.Key).Cascade(CascadeMode.Stop).NotEmpty().MaximumLength(100)
            .Must(key => key == key.Trim());
        When(request => request.Action is FeatureFlagActions.EnableForAccounts or FeatureFlagActions.DisableForAccounts, () =>
        {
            RuleFor(request => request.Emails).NotEmpty();
            RuleForEach(request => request.Emails).NotEmpty().EmailAddress();
        });
        When(request => request.Action == FeatureFlagActions.Create, () =>
        {
            RuleFor(request => request.Description).NotEmpty().MaximumLength(500)
                .Must(description => description?.IndexOfAny(['\r', '\n']) < 0);
        });
    }
}

public class ListFeatureFlagsQueryValidator : AbstractValidator<ListFeatureFlagsQuery>
{
    public ListFeatureFlagsQueryValidator() => RuleFor(request => request.AdminId).NotEmpty();
}
