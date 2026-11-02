using MediatR;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Commands;

public sealed record RegisterOAuthClientCommand(string ClientName, string[] RedirectUris) : IRequest<OAuthClient>;

public sealed class RegisterOAuthClientCommandHandler(IGenericRepository<OAuthClient> clients, IUnitOfWork work)
    : IRequestHandler<RegisterOAuthClientCommand, OAuthClient>
{
    public async Task<OAuthClient> Handle(RegisterOAuthClientCommand request, CancellationToken cancellationToken)
    {
        var client = OAuthClient.Create(request.ClientName, request.RedirectUris);
        await clients.AddAsync(client, cancellationToken);
        await work.SaveChangesAsync(cancellationToken);
        return client;
    }
}
