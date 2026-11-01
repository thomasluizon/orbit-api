using FluentValidation;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Queries;

namespace Orbit.Application.Chat.Validators;

public sealed class GetLatestConversationQueryValidator : AbstractValidator<GetLatestConversationQuery>
{
    public GetLatestConversationQueryValidator()
    {
        RuleFor(request => request.UserId).NotEmpty();
        RuleFor(request => request.ConversationId).Must(id => id is null || id != Guid.Empty);
        RuleFor(request => request.BeforeOrder).Must(order => order is null || order > 0);
        RuleFor(request => request.PageSize).InclusiveBetween(1, 100);
    }
}

public sealed class DeleteConversationCommandValidator : AbstractValidator<DeleteConversationCommand>
{
    public DeleteConversationCommandValidator()
    {
        RuleFor(request => request.UserId).NotEmpty();
        RuleFor(request => request.ConversationId).NotEmpty();
    }
}

public sealed class GetConversationImageQueryValidator : AbstractValidator<GetConversationImageQuery>
{
    public GetConversationImageQueryValidator()
    {
        RuleFor(request => request.UserId).NotEmpty();
        RuleFor(request => request.ConversationId).NotEmpty();
        RuleFor(request => request.TurnId).NotEmpty();
    }
}
