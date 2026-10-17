using FluentValidation;

namespace Orbit.Application.Connections.Validators;

public sealed class AuthorizeNotionCommandValidator : AbstractValidator<AuthorizeNotionCommand>
{
    public AuthorizeNotionCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.RedirectUri).NotEmpty().MaximumLength(2048)
            .Must(value => Uri.IsWellFormedUriString(value, UriKind.Absolute));
    }
}

public sealed class CompleteNotionCommandValidator : AbstractValidator<CompleteNotionCommand>
{
    public CompleteNotionCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Code).NotEmpty().MaximumLength(4096);
        RuleFor(x => x.State).NotEmpty().Length(64).Must(value => value is not null && value.All(Uri.IsHexDigit));
        RuleFor(x => x.RedirectUri).NotEmpty().MaximumLength(2048)
            .Must(value => Uri.IsWellFormedUriString(value, UriKind.Absolute));
    }
}

public sealed class GetNotionConnectionQueryValidator : AbstractValidator<GetNotionConnectionQuery>
{
    public GetNotionConnectionQueryValidator() => RuleFor(x => x.UserId).NotEmpty();
}

public sealed class DisconnectNotionCommandValidator : AbstractValidator<DisconnectNotionCommand>
{
    public DisconnectNotionCommandValidator() => RuleFor(x => x.UserId).NotEmpty();
}

public sealed class GetNotionTasksQueryValidator : AbstractValidator<GetNotionTasksQuery>
{
    public GetNotionTasksQueryValidator() => RuleFor(x => x.UserId).NotEmpty();
}
