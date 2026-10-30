using FluentValidation;
using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Commands;

public sealed record DismissConversationOffersCommand(Guid UserId, Guid ConversationId,
    IReadOnlyList<string> OfferKeys) : IRequest<Result>;

public sealed class DismissConversationOffersCommandValidator : AbstractValidator<DismissConversationOffersCommand>
{
    public DismissConversationOffersCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.ConversationId).NotEmpty();
        RuleFor(x => x.OfferKeys).NotNull().Must(keys => keys is { Count: > 0 and <= 100 });
        RuleForEach(x => x.OfferKeys).NotEmpty().MaximumLength(200).Must(key => key is not null && !key.Any(char.IsControl));
    }
}

public sealed class DismissConversationOffersCommandHandler(IConversationOfferStore store)
    : IRequestHandler<DismissConversationOffersCommand, Result>
{
    public async Task<Result> Handle(DismissConversationOffersCommand request, CancellationToken ct)
    {
        await store.DismissAsync(request.UserId, request.ConversationId, request.OfferKeys, ct);
        return Result.Success();
    }
}
